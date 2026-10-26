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
