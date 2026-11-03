using ZlmSharp.Ai.Analysis;

namespace ZlmSharp.Ai;

/// <summary>
/// 分析器的运行状态描述（模型、类别、后端等），供 API 展示（<c>yoloInfo</c> / <c>getAnalyzerList</c>）。
/// </summary>
public sealed record AnalyzerDescriptor(
    string Id,
    string DisplayName,
    bool Enabled,
    bool Ready,
    string? Model,
    string? ModelDir,
    string? Device,
    string? Backend,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> AvailableModels,
    IReadOnlyList<string> LoadedModels,
    IReadOnlyDictionary<string, string> Settings,
    string? Error = null);

/// <summary>
/// 分析器可选能力：向宿主暴露运行状态（模型/后端是否就绪），供调度与 API 展示。
/// 不实现时宿主只能展示 id / 名称 / 是否启用。
/// </summary>
public interface IAnalyzerInfo
{
    AnalyzerDescriptor Describe();
}

/// <summary>分析器与模型清单（<c>getAnalyzerList</c> / <c>yoloInfo</c> 的数据来源）。</summary>
public interface IAnalyzerCatalog
{
    IReadOnlyList<AnalyzerDescriptor> List();

    AnalyzerDescriptor? Get(string analyzerId);
}

/// <summary>单图分析请求（<c>analyzeDetect</c> / <c>yoloDetect</c>）。</summary>
public sealed record ImageAnalysisRequest(string AnalyzerId)
{
    /// <summary>编码后的图片字节（如 P6 PPM），由实现负责解码。</summary>
    public ReadOnlyMemory<byte>? Image { get; init; }

    /// <summary>或从流地址（RTSP/RTMP/HTTP-FLV）抓一帧。</summary>
    public string? SourceUrl { get; init; }

    public string? Model { get; init; }

    public float? Confidence { get; init; }
}

/// <summary>单图分析服务：校验参数 → 取图（body 或 url）→ 调用分析器。由扩展包实现。</summary>
public interface IImageAnalysisService
{
    /// <summary>支持的图片格式（如 <c>ppm</c>），仅用于文档与错误提示。</summary>
    IReadOnlyList<string> ImageFormats { get; }

    /// <summary>
    /// 分析一张图片。模型名非法抛 <see cref="ArgumentException"/> / <see cref="FileNotFoundException"/>，
    /// 图片解码失败抛 <see cref="InvalidDataException"/>，消息直接返回给调用方。
    /// </summary>
    Task<AnalysisResult> AnalyzeAsync(ImageAnalysisRequest request, CancellationToken cancellationToken);
}
