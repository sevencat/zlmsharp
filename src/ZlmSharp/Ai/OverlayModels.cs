namespace ZlmSharp.Ai;

/// <summary>一路叠加流的状态。</summary>
public enum OverlayState
{
    /// <summary>进程已拉起、尚未写出第一帧（ffmpeg 解码/编码预热，通常持续数秒，此时 <c>frames=0</c> 属正常）。</summary>
    Starting = 0,

    /// <summary>已产出首帧并持续编码输出。</summary>
    Running = 1,

    Stopped = 2,

    Failed = 3,
}

/// <summary>开始（或重启）一路叠加流的请求。</summary>
public sealed record OverlayRequest(string Vhost, string App, string Stream)
{
    /// <summary>
    /// 只用这些分析器的结果绘制（可多选，如 <c>yolo,ocr</c>）；
    /// 空表示全部有叠加输出的分析器。
    /// </summary>
    public IReadOnlyList<string> AnalyzerIds { get; init; } = [];

    /// <summary>
    /// 该叠加流使用的推理模型（YOLO 的模型文件名或路径，如 <c>helmet.onnx</c>）；
    /// 空表示用分析器配置的默认模型。只影响这一路流，不改变全局默认。
    /// </summary>
    public string? Model { get; init; }

    public int? Fps { get; init; }

    public int BitrateKbps { get; init; }

    public int LabelScale { get; init; } = 2;

    /// <summary>兼容单值访问：只选了一个分析器时返回它，多选/未选返回 null。</summary>
    public string? AnalyzerId => AnalyzerIds.Count == 1 ? AnalyzerIds[0] : null;
}

/// <summary>一路叠加流（<c>{stream}_ai</c>）的运行状态。</summary>
public sealed record OverlayStatus(
    string Vhost,
    string App,
    string Stream,
    string OverlayStream,
    string InputUrl,
    string OutputUrl,
    OverlayState State,
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    long Frames,
    DateTimeOffset StartedAt,
    double ElapsedMs,
    string? Error)
{
    public bool Running => State is OverlayState.Starting or OverlayState.Running;
}

/// <summary>
/// 结果叠加控制器（<c>startYoloOverlay</c> / <c>startOverlay</c> 的后端）。
/// 由扩展包实现；未实现时 API 按“未接入”处理。
/// </summary>
public interface IOverlayController
{
    /// <summary>叠加输出流名（默认 <c>{stream}_ai</c>，由 <c>ZLM_OVERLAY_SUFFIX</c> 配置）。</summary>
    string OverlayStreamName(string stream);

    /// <summary>是否是叠加流（定时检测应跳过，避免对叠加结果再分析形成回路）。</summary>
    bool IsOverlayStream(string? stream);

    /// <summary>
    /// 开始（或重启）一路叠加流。源流必须在线且已收到关键帧（需要分辨率）。
    /// </summary>
    /// <exception cref="ArgumentException">参数非法（含对叠加流再叠加）。</exception>
    /// <exception cref="InvalidOperationException">源流不在线/分辨率未知/端口未开启。</exception>
    OverlayStatus Start(OverlayRequest request);

    /// <summary>停止一路叠加流；未在运行返回 false。</summary>
    bool Stop(string vhost, string app, string stream);

    OverlayStatus? Get(string vhost, string app, string stream);

    IReadOnlyList<OverlayStatus> List(string vhost = "", string app = "", string stream = "");
}
