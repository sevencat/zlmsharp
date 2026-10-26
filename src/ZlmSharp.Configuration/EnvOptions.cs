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
    public const string YoloModelDirKey = "ZLM_YOLO_MODEL_DIR";
    public const string YoloModelKey = "ZLM_YOLO_MODEL";
    public const string YoloEndpointKey = "ZLM_YOLO_ENDPOINT";
    public const string YoloDeviceKey = "ZLM_YOLO_DEVICE";
    public const string YoloConfidenceKey = "ZLM_YOLO_CONFIDENCE";
    public const string YoloInputWidthKey = "ZLM_YOLO_INPUT_WIDTH";
    public const string YoloInputHeightKey = "ZLM_YOLO_INPUT_HEIGHT";
    public const string YoloCpuThreadsKey = "ZLM_YOLO_CPU_THREADS";
    public const string YoloSampleIntervalMsKey = "ZLM_YOLO_SAMPLE_INTERVAL_MS";

    public string FfmpegBin { get; set; } = string.Empty;

    public string FfprobeBin { get; set; } = string.Empty;

    public string RecordRoot { get; set; } = string.Empty;

    /// <summary>模型目录；<see cref="YoloModel"/> 只写文件名时在此目录下查找。</summary>
    public string YoloModelDir { get; set; } = string.Empty;

    public string YoloModel { get; set; } = string.Empty;

    public string YoloEndpoint { get; set; } = string.Empty;

    /// <summary>推理设备：<c>cpu</c> / <c>vulkan</c> / <c>metal</c> / <c>auto</c>。</summary>
    public string YoloDevice { get; set; } = "cpu";

    public float YoloConfidence { get; set; } = 0.25f;

    public int YoloInputWidth { get; set; } = 640;

    public int YoloInputHeight { get; set; } = 640;

    /// <summary>CPU 推理线程上限，0 表示用库默认值。</summary>
    public int YoloCpuThreads { get; set; }

    /// <summary>同一路流两次检测的最小间隔（毫秒）。</summary>
    public int YoloSampleIntervalMs { get; set; } = 1000;

    /// <summary>配置了模型路径即视为启用 YOLO。</summary>
    public bool YoloEnabled => !string.IsNullOrWhiteSpace(YoloModel);

    /// <summary>
    /// 模型绝对路径：
    /// 绝对路径原样使用；只写文件名时拼到模型目录；其它相对路径按 exe 目录解析。未配置返回空串。
    /// </summary>
    public string ResolveYoloModelPath()
    {
        var path = YoloModel.Trim();
        if (path.Length == 0)
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var dir = ResolveYoloModelDir();
        if (dir.Length > 0 && !path.Contains('/') && !path.Contains('\\'))
        {
            return Path.Combine(dir, path);
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    /// <summary>模型目录绝对路径（相对路径按 exe 目录解析）；未配置返回空串。</summary>
    public string ResolveYoloModelDir()
    {
        var dir = YoloModelDir.Trim();
        if (dir.Length == 0)
        {
            return string.Empty;
        }

        return Path.IsPathRooted(dir) ? Path.GetFullPath(dir) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dir));
    }

    public IReadOnlyDictionary<string, string> Raw { get; set; } = new Dictionary<string, string>();

    /// <summary>ffmpeg 可执行路径：未配置时返回 <c>ffmpeg</c>（交给 PATH）。</summary>
    public string ResolveFfmpegPath() => string.IsNullOrWhiteSpace(FfmpegBin) ? "ffmpeg" : FfmpegBin.Trim();

    public string ResolveFfprobePath() => string.IsNullOrWhiteSpace(FfprobeBin) ? "ffprobe" : FfprobeBin.Trim();
}
