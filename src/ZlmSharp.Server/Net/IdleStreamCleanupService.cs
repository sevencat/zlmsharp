using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Server.Hooks;

namespace ZlmSharp.Server.Net;

/// <summary>
/// 无人观看回收：每隔几秒扫描一次，把「超过 <c>general.streamNoneReaderDelayMS</c> 没人看」的流关掉。
/// 是否关闭正在推流/代理的流由配置决定：
/// 1. 没有发布者会话的残留流 → 直接回收；
/// 2. <c>protocol.auto_close=1</c> → 直接回收（并踢掉推流端）；
/// 3. 配了 <c>hook.on_stream_none_reader</c> → 由外部服务决定（返回 code 0 才关）；
/// 4. 其它情况 → 保留（与 ZLM 默认一致）。
/// </summary>
internal sealed class IdleStreamCleanupService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private readonly AppOptions _options;
    private readonly MediaSourceRegistry _registry;
    private readonly MediaSessionRegistry _sessions;
    private readonly MediaHookService _hook;
    private readonly ILogger _logger;

    public IdleStreamCleanupService(
        AppOptions options,
        MediaSourceRegistry registry,
        MediaSessionRegistry sessions,
        MediaHookService hook,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _registry = registry;
        _sessions = sessions;
        _hook = hook;
        _logger = loggerFactory.CreateLogger("ZlmSharp.IdleCleanup");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delayMs = _options.General.StreamNoneReaderDelayMS;
        if (delayMs <= 0)
        {
            _logger.LogInformation("无人观看回收未启用（streamNoneReaderDelayMS={Delay}）", delayMs);
            return;
        }

        var delay = TimeSpan.FromMilliseconds(delayMs);
        var hasNoneReaderHook = _options.Hook.Enable && _options.Hook.OnStreamNoneReader.Length > 0;
        _logger.LogInformation(
            "无人观看回收启动：延迟 {Delay}ms，auto_close={AutoClose}，none_reader_hook={Hook}",
            delayMs,
            _options.Protocol.AutoClose,
            hasNoneReaderHook);

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SweepAsync(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "无人观看回收扫描失败");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SweepAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        HashSet<StreamKey>? closable = null;

        foreach (var source in _registry.Sources.ToArray())
        {
            if (source.IsClosed || source.ReaderCount > 0 || now - source.IdleSince < delay)
            {
                continue;
            }

            var publishers = _sessions.Snapshot(
                session => session.Type == MediaSessionType.Publisher && session.Key == source.Key);

            if (publishers.Count == 0)
            {
                // 发布者已消失但流还留着（异常退出、代理断开）：直接回收
                (closable ??= []).Add(source.Key);
                continue;
            }

            var close = _options.Protocol.AutoClose;
            if (!close && _options.Hook.Enable && _options.Hook.OnStreamNoneReader.Length > 0)
            {
                close = await _hook
                    .ShouldCloseNoneReaderAsync(source.Key, source.Schema, publishers[0].Id, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!close)
            {
                continue;
            }

            // 先断推流端再摘流，避免发布者继续往已关闭的流写帧
            _sessions.Kick(session => session.Type == MediaSessionType.Publisher && session.Key == source.Key);
            (closable ??= []).Add(source.Key);

            _logger.LogInformation(
                "无人观看回收 {Key}（闲置 {Idle:F1}s，schema={Schema}）",
                source.Key,
                (now - source.IdleSince).TotalSeconds,
                source.Schema);
        }

        if (closable is not null)
        {
            _registry.CleanupIdle(delay, source => closable.Contains(source.Key), now);
        }
    }
}
