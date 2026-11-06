using System.Collections.Concurrent;
using ZlmSharp.Ai;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Core;

namespace ZlmSharp.Extra.Yolo;

/// <summary>YOLO 作为通用分析层的一个实现：检测 + IoU 跟踪，输出 <see cref="AnalysisKind.Box"/> 图元。</summary>
public sealed class YoloAnalyzer : IStreamAnalyzer, IAnalyzerInfo, IImageAnalysisCapable
{
    public const string AnalyzerId = "yolo";

    private readonly YoloDetectorService _detector;
    private readonly Analysis.AnalysisModelSelection? _modelSelection;
    private readonly ConcurrentDictionary<string, TrackerState> _trackers = new(StringComparer.Ordinal);

    public YoloAnalyzer(YoloDetectorService detector, Analysis.AnalysisModelSelection? modelSelection = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detector = detector;
        _modelSelection = modelSelection;
    }

    public string Id => AnalyzerId;

    public string DisplayName => "YOLO 目标检测";

    public bool Enabled => _detector.Enabled;

    public async Task<AnalysisResult> AnalyzeAsync(FrameSample frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        try
        {
            // 该流若通过 startOverlay&model=xxx.onnx 指定了模型，就用它；否则走全局默认模型
            var model = _modelSelection?.Get(frame.Key);
            var result = await _detector.DetectAsync(frame.Image, cancellationToken: cancellationToken, model: model)
                .ConfigureAwait(false);

            // 跨帧分配稳定 ID（同一路流一个跟踪器；分辨率变化时重建，避免旧框坐标串台）
            var tracked = GetTracker(frame.Key, result.Width, result.Height).Update(result.Objects);

            return new AnalysisResult(
                AnalyzerId,
                frame.Key,
                DateTimeOffset.Now,
                result.ElapsedMs,
                result.Width,
                result.Height,
                tracked.Select(ToPrimitive).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ErrorResult(frame.Key, exception.Message);
        }
    }

    public void ValidateModel(string? model) => _detector.ResolveModelPath(model);

    /// <remarks>单图分析（API 入口）不做错误兜底：加载/推理失败直接抛出，由端点转成错误响应。</remarks>
    public async Task<AnalysisResult> AnalyzeImageAsync(RgbImage image, string? model, float? confidence, CancellationToken cancellationToken)
    {
        var key = StreamKey.Create(null, "image", "single");
        var result = await _detector.DetectAsync(image, confidence, model, cancellationToken).ConfigureAwait(false);
        return new AnalysisResult(
            AnalyzerId,
            key,
            DateTimeOffset.Now,
            result.ElapsedMs,
            result.Width,
            result.Height,
            result.Objects.Select(ToPrimitive).ToArray());
    }

    public AnalyzerDescriptor Describe()
    {
        YoloModelSummary? summary = null;
        if (_detector.Enabled)
        {
            summary = _detector.ReadModelSummary();
        }

        // 模型配置了但读不到元信息（路径不存在 / 格式不受支持 / 后端未接入）
        var error = _detector.Enabled && summary is null
            ? "无法读取 YOLO 模型（路径或格式不受支持）"
            : null;

        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["input_width"] = _detector.Options.InputWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["input_height"] = _detector.Options.InputHeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["confidence"] = _detector.Options.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["backend"] = _detector.BackendName,
        };

        if (summary is not null)
        {
            settings["task"] = summary.Task;
        }

        return new AnalyzerDescriptor(
            AnalyzerId,
            DisplayName,
            _detector.Enabled,
            _detector.BackendAvailable,
            _detector.ModelPath.Length > 0 ? _detector.ModelPath : null,
            _detector.ModelDir.Length > 0 ? _detector.ModelDir : null,
            _detector.Device,
            _detector.BackendName,
            summary?.ClassNames ?? [],
            _detector.AvailableModels,
            _detector.LoadedModels,
            settings,
            error);
    }

    private static AnalysisResult ErrorResult(StreamKey key, string message) => new(
        AnalyzerId,
        key,
        DateTimeOffset.Now,
        0,
        0,
        0,
        [],
        message);

    private static AnalysisPrimitive ToPrimitive(YoloObject item) => new(
        AnalysisKind.Box,
        item.ClassName,
        item.Confidence,
        item.TrackId == 0 ? null : (int)item.TrackId,
        item.X,
        item.Y,
        item.Width,
        item.Height,
        null,
        null,
        new Dictionary<string, string> { ["classId"] = item.ClassId.ToString() });

    private ObjectTracker GetTracker(StreamKey key, int width, int height)
    {
        var id = $"{key.Vhost}/{key.App}/{key.Stream}";
        if (_trackers.TryGetValue(id, out var state) && state.Width == width && state.Height == height)
        {
            return state.Tracker;
        }

        var tracker = new ObjectTracker();
        _trackers[id] = new TrackerState(tracker, width, height);
        return tracker;
    }

    private sealed record TrackerState(ObjectTracker Tracker, int Width, int Height);
}
