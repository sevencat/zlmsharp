using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Server.Net;

/// <summary>
/// 按需拉流代理：<c>[proxy] on_demand_list</c> 预置 <c>app/stream=url</c>，平时不拉流。
/// 播放端（HTTP-FLV / RTMP play / RTSP play）点播到这些流时由协议层调 <see cref="EnsureStarted"/> 才真正开始拉；
/// 拉流中断时只要还有观众就按 <c>retry_delay_ms</c> 一直重连（由 <see cref="RtmpPullProxyManager"/> 负责）；
/// 观众全走光后再把这路代理停掉，回到"待触发"状态。
/// </summary>
/// <remarks>
/// 「有没有观众」用 <see cref="MediaSource.ReaderCount"/>（当前观看者）判断，停止条件见
/// <see cref="OnDemandProxyPolicy.ShouldStop"/>：
/// 有过观众 → 空闲超过 <c>on_demand_idle_ms</c> 停；
/// 从没有过观众（源连不上/观众等超时走了）→ 启动超过 <c>on_demand_max_wait_ms</c> 停。
/// </remarks>
internal sealed class OnDemandProxyService : BackgroundService, IOnDemandProxy
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly MediaSourceRegistry _registry;
    private readonly RtmpPullProxyManager _proxies;
    private readonly ILogger _logger;
    private readonly Dictionary<StreamKey, string> _targets;
    private readonly ConcurrentDictionary<StreamKey, DateTimeOffset> _started = new();
    private readonly object _gate = new();
    private readonly TimeSpan _idleDelay;
    private readonly TimeSpan _maxWait;

    public OnDemandProxyService(
        ProxyOptions options,
        MediaSourceRegistry registry,
        RtmpPullProxyManager proxies,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(proxies);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _registry = registry;
        _proxies = proxies;
        _logger = loggerFactory.CreateLogger("ZlmSharp.OnDemandProxy");
        _idleDelay = TimeSpan.FromMilliseconds(Math.Max(1_000, options.OnDemandIdleMS));
        _maxWait = TimeSpan.FromMilliseconds(Math.Max(1_000, options.OnDemandMaxWaitMS));

        _targets = new Dictionary<StreamKey, string>();
        foreach (var (key, url) in RtmpPullProxyManager.ParseList(options.OnDemandList))
        {
            _targets[key] = url;
        }
    }

    /// <summary>配置了几路按需拉流（0 表示未启用）。</summary>
    public int Count => _targets.Count;

    /// <summary>当前正在拉的按需流数量。</summary>
    public int ActiveCount => _started.Count;

    public bool IsConfigured(StreamKey key) => _targets.ContainsKey(key);

    /// <summary>按需拉流的待触发列表（只读，便于诊断/测试）。</summary>
    public IReadOnlyDictionary<StreamKey, string> Targets => _targets;

    public bool EnsureStarted(StreamKey key)
    {
        if (!_targets.TryGetValue(key, out var url) || _proxies.IsPulling(key))
        {
            return false;
        }

        lock (_gate)
        {
            // 两个播放端可能同时触发，二次确认后再启动
            if (_proxies.IsPulling(key))
            {
                return false;
            }

            try
            {
                _proxies.Start(key.Vhost, key.App, key.Stream, url);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                _logger.LogWarning("按需拉流启动失败 {Key} ← {Url}：{Message}", key, url, exception.Message);
                return false;
            }

            _started[key] = DateTimeOffset.UtcNow;
            _logger.LogInformation("按需拉流启动 {Key} ← {Url}（等待观众）", key, url);
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_targets.Count == 0)
        {
            _logger.LogInformation("按需拉流未启用（proxy.on_demand_list 为空）");
            return;
        }

        _logger.LogInformation(
            "按需拉流已就绪：{Count} 路（无人观看 {Idle}ms 后停止，启动后 {MaxWait}ms 仍无观众也停止）",
            _targets.Count,
            _idleDelay.TotalMilliseconds,
            _maxWait.TotalMilliseconds);

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    Sweep();
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "按需拉流扫描失败");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Sweep()
    {
        if (_started.IsEmpty)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var pair in _started.ToArray())
        {
            var key = pair.Key;

            // 代理被外部停掉（delStreamProxy / 服务关闭）：清掉记录
            if (!_proxies.IsPulling(key))
            {
                _started.TryRemove(key, out _);
                continue;
            }

            // 本地流还没建出来或正在重建：这一轮无法判断观众数，留给下一轮
            if (!_registry.TryGet(key, out var source) || source is null || source.IsClosed)
            {
                continue;
            }

            if (!OnDemandProxyPolicy.ShouldStop(
                    source.ReaderCount,
                    source.TotalReaderCount,
                    now - source.IdleSince,
                    now - pair.Value.UtcDateTime,
                    _idleDelay,
                    _maxWait,
                    out var reason))
            {
                continue;
            }

            _proxies.Stop(key);
            _started.TryRemove(key, out _);
            _logger.LogInformation(
                "按需拉流停止 {Key}（{Reason}，已拉 {Elapsed:F0}s）",
                key,
                reason,
                (now - pair.Value.UtcDateTime).TotalSeconds);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var key in _started.Keys)
        {
            _proxies.Stop(key);
        }

        _started.Clear();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>按需拉流的停止判定（纯函数，便于单测）。</summary>
public static class OnDemandProxyPolicy
{
    /// <summary>
    /// 是否应该停掉一路按需拉流：
    /// 1. 还有观众 → 不停（断线由拉流循环一直重连）；
    /// 2. 有过观众且空闲超过 <paramref name="idleDelay"/> → 停；
    /// 3. 从没有过观众（源连不上、观众等超时走了）且启动超过 <paramref name="maxWait"/> → 停。
    /// </summary>
    public static bool ShouldStop(
        int readerCount,
        long totalReaderCount,
        TimeSpan idleFor,
        TimeSpan sinceStart,
        TimeSpan idleDelay,
        TimeSpan maxWait,
        out string reason)
    {
        reason = string.Empty;

        if (readerCount > 0)
        {
            return false;
        }

        if (totalReaderCount > 0)
        {
            if (idleFor < idleDelay)
            {
                return false;
            }

            reason = $"无人观看 {idleFor.TotalSeconds:F0}s";
            return true;
        }

        if (sinceStart < maxWait)
        {
            return false;
        }

        reason = $"启动 {sinceStart.TotalSeconds:F0}s 仍无观众";
        return true;
    }
}
