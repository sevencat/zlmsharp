namespace ZlmSharp.Api;

/// <summary>通用分析结果的 API 模型（<c>analyzeResult</c>），与 YOLO 专用接口解耦。</summary>
public sealed record ApiAnalysisPoint(double X, double Y);

/// <summary>一个分析图元：<c>kind</c> = box / polygon / text / mask。</summary>
public sealed record ApiAnalysisPrimitive(
    string Kind,
    string Label,
    double Score,
    int? TrackId,
    double? X,
    double? Y,
    double? Width,
    double? Height,
    IReadOnlyList<ApiAnalysisPoint>? Points,
    string? Text);

public sealed record ApiAnalysisData(
    string Analyzer,
    string Vhost,
    string App,
    string Stream,
    long At,
    long AgeMs,
    long ElapsedMs,
    int Width,
    int Height,
    int Count,
    IReadOnlyList<ApiAnalysisPrimitive> Primitives,
    string? Error);

public sealed record ApiAnalyzeResultResponse(int Code, string? Msg, IReadOnlyList<ApiAnalysisData>? Data);

/// <summary>单图分析（<c>analyzeDetect</c>）的结果，字段与 <see cref="ApiAnalysisData"/> 一致。</summary>
public sealed record ApiAnalyzeDetectResponse(int Code, string? Msg, ApiAnalysisData? Data);

/// <summary>一个分析器的运行状态（<c>getAnalyzerList</c>），含第三方接入的算法。</summary>
public sealed record ApiAnalyzerInfo(
    string Id,
    string DisplayName,
    bool Enabled,
    bool Ready,
    string Model,
    string ModelDir,
    string Device,
    string Backend,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> LoadedModels,
    IReadOnlyDictionary<string, string> Settings,
    string? Error);

public sealed record ApiAnalyzerListResponse(int Code, IReadOnlyList<ApiAnalyzerInfo> Data);

/// <summary>一路叠加流（<c>{stream}_ai</c>）的状态（通用端点 <c>startOverlay</c> 等）。</summary>
public sealed record ApiOverlayData(
    string Vhost,
    string App,
    string Stream,
    string OverlayStream,
    string Input,
    string Output,
    string State,
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    long Frames,
    long StartedAt,
    long ElapsedMs,
    string? Error);

public sealed record ApiOverlayResponse(int Code, string? Msg, ApiOverlayData? Data);

public sealed record ApiOverlayListResponse(int Code, IReadOnlyList<ApiOverlayData> Data);

public sealed record ApiOverlayFlagData(bool Flag);

public sealed record ApiOverlayFlagResponse(int Code, ApiOverlayFlagData Data);
