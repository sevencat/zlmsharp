using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Configuration;
using ZlmSharp.Core;

namespace ZlmSharp.Ai;

/// <summary>
/// 在线流定时 YOLO 检测：每 <c>ZLM_YOLO_SAMPLE_INTERVAL_MS</c> 从常驻解码器取最新一帧，
/// 推理后把结果写进 <see cref="YoloResultStore"/>（<c>getYoloResult</c> 读取）。
/// </summary>
/// <remarks>
/// 解码走 <see cref="FfmpegDecoderPool"/>：一路流一个常驻 ffmpeg 子进程，持续输出 RGB24，
/// 采样时只取最新帧，不再为每次采样新建进程。信号量保证同一时刻只有一路在推理（SimdYolo 会话不可重入）。
/// </remarks>
internal sealed class StreamYoloMonitor : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromSeconds(8);

    private readonly YoloDetectorService _detector;
    private readonly MediaSourceRegistry _registry;
    private readonly YoloResultStore _store;
    private readonly AnalysisResultStore _analyses;
    private readonly FfmpegDecoderPool _decoders;
    private readonly AppOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<StreamKey, DateTime> _lastRun = [];
    private readonly Dictionary<StreamKey, TrackerState> _trackers = [];

    public StreamYoloMonitor(
        YoloDetectorService detector,
        MediaSourceRegistry registry,
        YoloResultStore store,
        AnalysisResultStore analyses,
        FfmpegDecoderPool decoders,
        AppOptions options,
        ILoggerFactory loggerFactory)
    {
        _detector = detector;
        _registry = registry;
        _store = store;
        _analyses = analyses;
        _decoders = decoders;
        _options = options;
        _logger = loggerFactory.CreateLogger("ZlmSharp.Yolo");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_detector.Enabled)
        {
            _logger.LogInformation("YOLO 定时检测未启用（.env 未配置 ZLM_YOLO_MODEL）");
            return;
        }

        if (_options.Http.Port <= 0)
        {
            _logger.LogWarning("YOLO 定时检测需要本机 HTTP 端口（HTTP-FLV 抽帧），当前 http.port=0，已跳过");
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(200, _options.Env.YoloSampleIntervalMs));
        _logger.LogInformation(
            "YOLO 定时检测启动：间隔 {Interval}ms，模型 {Model}，设备 {Device}",
            interval.TotalMilliseconds,
            Path.GetFileName(_detector.ModelPath),
            _detector.Device);

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
                        _lastRun.Remove(key);
                        _trackers.Remove(key);
                        _decoders.Remove(key);
                        _store.Remove(key.Vhost, key.App, key.Stream);
                        _analyses.RemoveStream(key);
                        continue;
                    }

                    // 叠加流是 YOLO 的输出，不能再对它做检测（否则会无限套娃）
                    if (YoloOverlayManager.IsOverlayStream(key.Stream))
                    {
                        continue;
                    }

                    if (!ShouldSample(key, interval))
                    {
                        continue;
                    }

                    await SampleAsync(source, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool ShouldSample(StreamKey key, TimeSpan interval)
    {
        var now = DateTime.UtcNow;
        if (_lastRun.TryGetValue(key, out var last) && now - last < interval)
        {
            return false;
        }

        _lastRun[key] = now;
        return true;
    }

    private async Task SampleAsync(MediaSource source, CancellationToken cancellationToken)
    {
        var key = source.Key;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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

            var result = await _detector.DetectAsync(image, cancellationToken: cancellationToken).ConfigureAwait(false);

            // 跨帧分配稳定 ID（同一路流一个跟踪器；分辨率变化时重建，避免旧框坐标串台）
            var tracker = GetTracker(key, result.Width, result.Height);
            var tracked = tracker.Update(result.Objects);

            var snapshot = new YoloSnapshot(
                key.Vhost,
                key.App,
                key.Stream,
                DateTimeOffset.UtcNow,
                result.ElapsedMs,
                result.Width,
                result.Height,
                tracked,
                null);
            _store.Set(snapshot);
            _analyses.Set(YoloAnalyzer.FromSnapshot(snapshot));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "YOLO 抽帧/检测失败 {Key}", key);
            var snapshot = new YoloSnapshot(
                key.Vhost,
                key.App,
                key.Stream,
                DateTimeOffset.UtcNow,
                0,
                0,
                0,
                [],
                exception.Message);
            _store.Set(snapshot);
            _analyses.Set(YoloAnalyzer.FromSnapshot(snapshot));
        }
        finally
        {
            _gate.Release();
        }
    }

    private ObjectTracker GetTracker(StreamKey key, int width, int height)
    {
        if (_trackers.TryGetValue(key, out var state) && state.Width == width && state.Height == height)
        {
            return state.Tracker;
        }

        var tracker = new ObjectTracker();
        _trackers[key] = new TrackerState(tracker, width, height);
        return tracker;
    }

    private sealed record TrackerState(ObjectTracker Tracker, int Width, int Height);

    private string BuildFlvUrl(StreamKey key)
    {
        var prefix = key.Vhost == StreamKey.DefaultVhost ? string.Empty : "/" + key.Vhost;
        return $"http://127.0.0.1:{_options.Http.Port}{prefix}/{key.App}/{key.Stream}.live.flv";
    }
}
