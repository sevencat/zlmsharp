using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZlmSharp.Ai;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtmp;
using ZlmSharp.Recording;
using ZlmSharp.Server.Hooks;

namespace ZlmSharp.Server.Net;

/// <summary>
/// 无人观看回收：每隔几秒扫描一次，把「超过 <c>general.streamNoneReaderDelayMS</c> 没人看」的流关掉。
/// 是否关闭正在推流/代理的流由配置决定：
/// 1. 正在录像的流 → 保留（没人看也要把录像写完）；
/// 2. 叠加输出流 → 永不自动回收（生命周期归 stopOverlay）；
/// 3. 拉流代理 → 按需代理（<c>[proxy] on_demand_list</c>）由 OnDemandProxyService 按观众数关闭；
///    普通代理由 <c>keep_on_none_reader</c>（或单路 <c>keep=1</c>）决定，默认随 auto_close 回收后自动重连；
/// 4. 没有发布者会话的残留流 → 直接回收；
/// 5. <c>protocol.auto_close=1</c> → 直接回收（并踢掉推流端）；
/// 6. 配了 <c>hook.on_stream_none_reader</c> → 由外部服务决定（返回 code 0 才关）；
/// 7. 其它情况 → 保留（与 ZLM 默认一致）。
/// </summary>
internal sealed class IdleStreamCleanupService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private readonly AppOptions _options;
    private readonly MediaSourceRegistry _registry;
    private readonly MediaSessionRegistry _sessions;
    private readonly MediaHookService _hook;
    private readonly IOverlayController? _overlays;
    private readonly RecorderManager? _recorder;
    private readonly RtmpPullProxyManager? _proxies;
    private readonly IOnDemandProxy? _onDemand;
    private readonly ILogger _logger;

    public IdleStreamCleanupService(
        AppOptions options,
        MediaSourceRegistry registry,
        MediaSessionRegistry sessions,
        MediaHookService hook,
        ILoggerFactory loggerFactory,
        IOverlayController? overlays = null,
        RecorderManager? recorder = null,
        RtmpPullProxyManager? proxies = null,
        IOnDemandProxy? onDemand = null)
    {
        _options = options;
        _registry = registry;
        _sessions = sessions;
        _hook = hook;
        _overlays = overlays;
        _recorder = recorder;
        _proxies = proxies;
        _onDemand = onDemand;
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

            // 正在录像的流即使无人观看也保留：录像不依赖观众，摘流会中断录制。
            if (IsRecording(source.Key))
            {
                continue;
            }

            // 拉流代理与叠加流的生命周期由各自管理器持有（delStreamProxy / stopOverlay），
            // 不能当残留流回收：观众短暂断开就摘流，代理/叠加都只能人工重启。
            if (IsManagedStream(source))
            {
                continue;
            }

            var publishers = _sessions.Snapshot(
                session => session.Type == MediaSessionType.Publisher && session.Key == source.Key);

            if (publishers.Count == 0)
            {
                // 发布者已消失但流还留着（推流端异常退出）：直接回收
                _logger.LogInformation(
                    "回收残留流 {Key}（发布者已消失，闲置 {Idle:F1}s，schema={Schema}）",
                    source.Key,
                    (now - source.IdleSince).TotalSeconds,
                    source.Schema);
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

    /// <summary>正在录像（含启动中）的流不参与无人观看回收。</summary>
    private bool IsRecording(StreamKey key) => _recorder?.Get(key)?.Recording == true;

    /// <summary>
    /// 受管流：叠加输出流永不回收；拉流代理只有显式设置 <c>keep_on_none_reader</c> 时才常驻，
    /// 否则按 <c>auto_close</c> / hook 规则回收（<see cref="RtmpPullProxyManager"/> 会自己重建并重连）。
    /// 按需拉流（<c>[proxy] on_demand_list</c>）的生命周期归 <c>OnDemandProxyService</c>：
    /// 它按"有没有观众"决定停止，这里若再按 auto_close 摘流会引发无谓的重连抖动。
    /// </summary>
    private bool IsManagedStream(MediaSource source) =>
        string.Equals(source.OriginType, "proxy", StringComparison.OrdinalIgnoreCase)
            ? _onDemand?.IsConfigured(source.Key) == true || _proxies?.IsKeptOnNoneReader(source.Key) == true
            : (_overlays?.IsOverlayStream(source.Key.Stream) ?? false);
}
