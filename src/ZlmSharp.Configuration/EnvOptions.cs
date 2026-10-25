namespace ZlmSharp.Configuration;

/// <summary>
/// <c>.env</c> 的强类型视图：机器相关/敏感/外部工具路径。
/// 未配置时返回空串，由使用方决定回退（例如 ffmpeg 从 PATH 查找）。
/// </summary>
public sealed class EnvOptions
{
    public const string FfmpegBinKey = "ZLM_FFMPEG_BIN";
    public const string FfprobeBinKey = "ZLM_FFPROBE_BIN";
    public const string RecordRootKey = "ZLM_RECORD_ROOT";
    public const string YoloModelKey = "ZLM_YOLO_MODEL";
    public const string YoloEndpointKey = "ZLM_YOLO_ENDPOINT";

    public string FfmpegBin { get; set; } = string.Empty;

    public string FfprobeBin { get; set; } = string.Empty;

    public string RecordRoot { get; set; } = string.Empty;

    public string YoloModel { get; set; } = string.Empty;

    public string YoloEndpoint { get; set; } = string.Empty;

    public IReadOnlyDictionary<string, string> Raw { get; set; } = new Dictionary<string, string>();

    /// <summary>ffmpeg 可执行路径：未配置时返回 <c>ffmpeg</c>（交给 PATH）。</summary>
    public string ResolveFfmpegPath() => string.IsNullOrWhiteSpace(FfmpegBin) ? "ffmpeg" : FfmpegBin.Trim();

    public string ResolveFfprobePath() => string.IsNullOrWhiteSpace(FfprobeBin) ? "ffprobe" : FfprobeBin.Trim();
}
