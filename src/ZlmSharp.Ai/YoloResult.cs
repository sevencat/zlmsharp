namespace ZlmSharp.Ai;

/// <summary>一个检测框（坐标为原图像素，左上角 + 宽高）。</summary>
public sealed record YoloObject(
    int ClassId,
    string ClassName,
    float Confidence,
    float X,
    float Y,
    float Width,
    float Height);

/// <summary>一次检测的结果。</summary>
public sealed record YoloResult(
    int Width,
    int Height,
    long ElapsedMs,
    IReadOnlyList<YoloObject> Objects);

/// <summary>模型元信息（读文件即可，不加载权重/后端）。</summary>
public sealed record YoloModelSummary(
    string Format,
    string Task,
    int ClassCount,
    IReadOnlyList<string> ClassNames);
