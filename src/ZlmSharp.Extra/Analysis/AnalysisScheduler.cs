using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZlmSharp.Ai;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Extra.Capture;
using ZlmSharp.Extra.Overlay;

namespace ZlmSharp.Extra.Analysis;

/// <summary>
/// 通用分析调度：定时从常驻解码器取最新一帧，交给所有已启用的分析器，
/// 再按 <see cref="AnalyzerRegistration.Outputs"/> 分发结果（快照 / 叠加缓存 / sink / 只跑不存）。
/// </summary>
/// <remarks>
/// 解码走 <see cref="FfmpegDecoderPool"/>：一路流一个常驻 ffmpeg 子进程，多个分析器共享同一帧。
/// 每个分析器一把信号量（推理会话通常不可重入），同一时刻一个分析器只处理一路流。
/// </remarks>
internal sealed class AnalysisScheduler : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromSeconds(8);

    private readonly AnalyzerRegistry _analyzers;
    private readonly AnalysisSinks _sinks;
    private readonly MediaSourceRegistry _registry;
    private readonly AnalysisResultStore _store;
    private readonly OverlayResultCache _overlayResults;
    private readonly FfmpegDecoderPool _decoders;
    private readonly IOverlayController _overlays;
    private readonly AppOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(StreamKey Key, string AnalyzerId), DateTime> _lastRun = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private long _skippedSamples;

    public AnalysisScheduler(
        AnalyzerRegistry analyzers,
        AnalysisSinks sinks,
        MediaSourceRegistry registry,
        AnalysisResultStore store,
        OverlayResultCache overlayResults,
        FfmpegDecoderPool decoders,
        IOverlayController overlays,
        AppOptions options,
        ILoggerFactory loggerFactory)
    {
        _analyzers = analyzers;
        _sinks = sinks;
        _registry = registry;
        _store = store;
        _overlayResults = overlayResults;
        _decoders = decoders;
        _overlays = overlays;
        _options = options;
        _logger = loggerFactory.CreateLogger("ZlmSharp.Analysis");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Env.ExtraEnable)
        {
            _logger.LogInformation("分析调度未启用（ZLM_EXTRA_ENABLE=0）");
            return;
        }

        var registrations = SelectRegistrations().ToArray();
        if (registrations.Length == 0)
        {
            _logger.LogInformation("分析调度未启动：没有已启用的分析器");
            return;
        }

        if (_options.Http.Port <= 0)
        {
            _logger.LogWarning("分析调度需要本机 HTTP 端口（HTTP-FLV 抽帧），当前 http.port=0，已跳过");
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(200, _options.Env.AnalysisSampleIntervalMs));
        _logger.LogInformation(
            "分析调度启动：分析器 {Analyzers}，全局间隔 {Interval}ms",
            string.Join(",", registrations.Select(item => item.Analyzer.Id)),
            interval.TotalMilliseconds);

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                foreach (var source in _registry.Sources.ToArray())
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var key = source.Key;
                    if (source.IsClosed)
                    {
                        _store.RemoveStream(key);
                        _overlayResults.RemoveStream(key);
                        _decoders.Remove(key);
                        continue;
                    }

                    // 叠加流是分析的输出，不能再对它做分析（否则会无限套娃）
                    if (_overlays.IsOverlayStream(key.Stream))
                    {
                        continue;
                    }

                    foreach (var registration in registrations)
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            return;
                        }

                        if (!IsSampleDue(key, registration, interval))
                        {
                            continue;
                        }

                        await SampleAsync(source, registration, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>按白名单（<c>ZLM_ANALYSIS_ENABLED</c>）与就绪状态挑选要调度的分析器。</summary>
    private IEnumerable<AnalyzerRegistration> SelectRegistrations()
    {
        var whitelist = _options.Env.AnalysisEnabledList;
        foreach (var registration in _analyzers.Enabled)
        {
            if (whitelist.Count > 0 && !whitelist.Contains(registration.Analyzer.Id))
            {
                _logger.LogInformation("分析器 {Id} 不在 ZLM_ANALYSIS_ENABLED 白名单内，跳过", registration.Analyzer.Id);
                continue;
            }

            // 后端未接入（如 YOLO 没有推理引擎）时跳过采样，避免每个采样周期都失败一次
            if (registration.Analyzer is IAnalyzerInfo info && !info.Describe().Ready)
            {
                var descriptor = info.Describe();
                _logger.LogWarning(
                    "分析器 {Id} 未就绪（模型或推理后端不可用，backend={Backend}），采样已跳过",
                    registration.Analyzer.Id,
                    descriptor.Backend ?? "unknown");
                continue;
            }

            yield return registration;
        }
    }

    /// <summary>只读判断是否到了采样时间（不写时间戳：忙时跳过不应推迟下一轮）。</summary>
    private bool IsSampleDue(StreamKey key, AnalyzerRegistration registration, TimeSpan fallbackInterval)
    {
        var interval = registration.SampleIntervalMs > 0
            ? TimeSpan.FromMilliseconds(Math.Max(200, registration.SampleIntervalMs))
            : fallbackInterval;

        return !_lastRun.TryGetValue((key, registration.Analyzer.Id), out var last) ||
               DateTime.UtcNow - last >= interval;
    }

    private void MarkSample(StreamKey key, AnalyzerRegistration registration) =>
        _lastRun[(key, registration.Analyzer.Id)] = DateTime.UtcNow;

    private async Task SampleAsync(MediaSource source, AnalyzerRegistration registration, CancellationToken cancellationToken)
    {
        var key = source.Key;
        var gate = _gates.GetOrAdd(registration.Analyzer.Id, _ => new SemaphoreSlim(1, 1));

        if (_options.Env.AnalysisSkipWhenBusy)
        {
            // 跳帧模式：上一轮还没算完就跳过本轮，不排队、不积压
            if (!gate.Wait(0))
            {
                Interlocked.Increment(ref _skippedSamples);
                _logger.LogDebug(
                    "分析器 {Id} 正忙，跳过本轮采样 {Key}（累计跳过 {Skipped} 次）",
                    registration.Analyzer.Id,
                    key,
                    Volatile.Read(ref _skippedSamples));
                return;
            }

            MarkSample(key, registration);
        }
        else
        {
            // 不跳帧模式：先记时间戳再排队，避免每个 tick 都排一个任务
            MarkSample(key, registration);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var width = source.Video.Width;
            var height = source.Video.Height;
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("流分辨率未知，常驻解码器无法确定帧尺寸");
            }

            var image = await _decoders
                .ReadFrameAsync(key, BuildFlvUrl(key), width, height, GrabTimeout, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new TimeoutException("常驻解码器未在超时时间内产出帧");

            var result = await registration.Analyzer.AnalyzeAsync(new FrameSample(key, image), cancellationToken)
                .ConfigureAwait(false);
            Dispatch(registration, result, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "分析器 {Id} 抽帧/分析失败 {Key}", registration.Analyzer.Id, key);

            // 出错也写入失败结果，调用方能看到失败原因而不是一直读到旧数据
            var failed = new AnalysisResult(
                registration.Analyzer.Id,
                key,
                DateTimeOffset.UtcNow,
                0,
                0,
                0,
                [],
                exception.Message);
            Dispatch(registration, failed, CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    private void Dispatch(AnalyzerRegistration registration, AnalysisResult result, CancellationToken cancellationToken)
    {
        if (registration.Outputs.HasFlag(AnalysisOutputs.Snapshot))
        {
            _store.Set(result);
        }

        if (registration.Outputs.HasFlag(AnalysisOutputs.Overlay))
        {
            _overlayResults.Set(result);
        }

        if (registration.Outputs.HasFlag(AnalysisOutputs.Sink))
        {
            foreach (var sink in _sinks.All)
            {
                try
                {
                    sink.PublishAsync(result, cancellationToken).GetAwaiter().GetResult();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "分析结果 sink 发布失败（{Sink}）", sink.GetType().Name);
                }
            }
        }

        if (result.Error is { Length: > 0 })
        {
            _logger.LogDebug("分析器 {Id} 报告错误：{Error}", registration.Analyzer.Id, result.Error);
        }
    }

    private string BuildFlvUrl(StreamKey key)
    {
        var prefix = key.Vhost == StreamKey.DefaultVhost ? string.Empty : "/" + key.Vhost;
        return $"http://127.0.0.1:{_options.Http.Port}{prefix}/{key.App}/{key.Stream}.live.flv";
    }
}
