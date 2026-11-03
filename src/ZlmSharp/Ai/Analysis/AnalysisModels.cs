using System.Collections.Concurrent;
using ZlmSharp.Core;

namespace ZlmSharp.Ai.Analysis;

/// <summary>分析结果的图元类型。新增分析器（OCR / 车牌 / 姿态）复用同一套图元。</summary>
public enum AnalysisKind
{
    /// <summary>矩形框（目标检测）。</summary>
    Box,

    /// <summary>多边形（区域布防、分割轮廓、OCR 文本四边形）。</summary>
    Polygon,

    /// <summary>纯文本（OCR 识别内容，可带锚点框）。</summary>
    Text,

    /// <summary>掩码（分割 / 热力图，坐标为归一化后的多段多边形）。</summary>
    Mask,
}

public readonly record struct AnalysisPoint(double X, double Y);

/// <summary>
/// 一个分析图元：坐标统一为原图像素；<see cref="Attributes"/> 放分析器私有字段（如 classId）。
/// </summary>
public sealed record AnalysisPrimitive(
    AnalysisKind Kind,
    string Label,
    double Score,
    int? TrackId = null,
    double? X = null,
    double? Y = null,
    double? Width = null,
    double? Height = null,
    IReadOnlyList<AnalysisPoint>? Points = null,
    string? Text = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

/// <summary>某路流在某个分析器下的一次结果。</summary>
public sealed record AnalysisResult(
    string AnalyzerId,
    StreamKey Key,
    DateTimeOffset At,
    long ElapsedMs,
    int Width,
    int Height,
    IReadOnlyList<AnalysisPrimitive> Primitives,
    string? Error = null)
{
    public static AnalysisResult Empty(string analyzerId, StreamKey key) =>
        new(analyzerId, key, DateTimeOffset.Now, 0, 0, 0, []);
}

/// <summary>送进分析器的一帧 RGB24 数据。</summary>
public sealed record FrameSample(StreamKey Key, RgbImage Image, long Dts = 0);

/// <summary>
/// 分析器（YOLO / OCR / 车牌 …）。实现应无状态或自行管理每路流的状态，
/// 由调度方按 <see cref="IStreamAnalyzer.Id"/> 决定是否对某路流启用。
/// </summary>
public interface IStreamAnalyzer
{
    /// <summary>稳定的分析器 id，出现在 API 与结果存储里（如 <c>yolo</c>）。</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>配置齐全、可以工作（比如模型文件存在）。</summary>
    bool Enabled { get; }

    Task<AnalysisResult> AnalyzeAsync(FrameSample frame, CancellationToken cancellationToken);
}

/// <summary>
/// 分析器可选能力：支持对单张图片分析（<c>analyzeDetect</c> / <c>yoloDetect</c>）。
/// 不实现的分析器只能被定时采样调度。
/// </summary>
public interface IImageAnalysisCapable
{
    /// <summary>校验模型名等参数；非法时抛 <see cref="ArgumentException"/> / <see cref="FileNotFoundException"/> 等，消息直接返回给调用方。</summary>
    void ValidateModel(string? model);

    Task<AnalysisResult> AnalyzeImageAsync(RgbImage image, string? model, float? confidence, CancellationToken cancellationToken);
}

/// <summary>把分析结果画到 RGB 帧上；一个流可以叠加多个 renderer。</summary>
public interface IOverlayRenderer
{
    string Id { get; }

    void Render(RgbImage image, AnalysisResult result);
}

/// <summary>
/// 结果消费方式（可组合）：默认只存快照；叠加模式只进叠加缓存；
/// <see cref="None"/> 表示只跑分析，不保存也不叠加（第三方在分析器内部自行处理结果）。
/// </summary>
[Flags]
public enum AnalysisOutputs
{
    None = 0,
    Snapshot = 1,
    Overlay = 2,
    Sink = 4,
}

/// <summary>一个分析器的调度参数与结果去向。</summary>
public sealed record AnalyzerRegistration(IStreamAnalyzer Analyzer)
{
    /// <summary>结果去向；默认只存快照（<c>analyzeResult</c> 可查）。</summary>
    public AnalysisOutputs Outputs { get; init; } = AnalysisOutputs.Snapshot;

    /// <summary>同一路流两次分析的最小间隔（毫秒）；0 表示用全局 <c>ZLM_ANALYSIS_SAMPLE_INTERVAL_MS</c>。</summary>
    public int SampleIntervalMs { get; init; }
}

/// <summary>可选：结果旁路（告警、入库、转发）。声明了 <see cref="AnalysisOutputs.Sink"/> 的分析器结果会推给所有 sink。</summary>
public interface IAnalysisSink
{
    ValueTask PublishAsync(AnalysisResult result, CancellationToken cancellationToken);
}

/// <summary>分析器注册表：新增分析器只需注册进来，调度与 API 不需要改。</summary>
public sealed class AnalyzerRegistry
{
    private readonly ConcurrentDictionary<string, AnalyzerRegistration> _analyzers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IStreamAnalyzer analyzer) => Register(new AnalyzerRegistration(analyzer));

    public void Register(AnalyzerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _analyzers[registration.Analyzer.Id] = registration;
    }

    public AnalyzerRegistration? Get(string id) =>
        _analyzers.TryGetValue(id, out var registration) ? registration : null;

    public IReadOnlyList<AnalyzerRegistration> All =>
        _analyzers.Values.OrderBy(item => item.Analyzer.Id, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<AnalyzerRegistration> Enabled => All.Where(item => item.Analyzer.Enabled).ToArray();
}

/// <summary>按 (流, 分析器) 保存最近一次结果，供 API 快照读取。</summary>
public sealed class AnalysisResultStore
{
    private readonly ConcurrentDictionary<string, AnalysisResult> _results = new(StringComparer.Ordinal);

    public void Set(AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _results[Id(result.Key, result.AnalyzerId)] = result;
    }

    public AnalysisResult? Get(StreamKey key, string analyzerId = "") =>
        _results.TryGetValue(Id(key, analyzerId), out var result) ? result : null;

    public IReadOnlyList<AnalysisResult> List(string vhost, string app, string stream, string analyzerId = "") =>
        _results.Values
            .Where(item => Matches(item.Key.Vhost, vhost) && Matches(item.Key.App, app) && Matches(item.Key.Stream, stream))
            .Where(item => analyzerId.Length == 0 || item.AnalyzerId.Equals(analyzerId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.At)
            .ToArray();

    public void Remove(StreamKey key, string analyzerId = "") => _results.TryRemove(Id(key, analyzerId), out _);

    public void RemoveStream(StreamKey key)
    {
        foreach (var pair in _results)
        {
            if (pair.Value.Key == key)
            {
                _results.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string Id(StreamKey key, string analyzerId) =>
        $"{key.Vhost}/{key.App}/{key.Stream}#{analyzerId}";

    private static bool Matches(string value, string filter) =>
        filter.Length == 0 || string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);
}
