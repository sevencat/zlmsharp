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
    public const string OverlaySuffixKey = "ZLM_OVERLAY_SUFFIX";
    public const string OverlayFramePolicyKey = "ZLM_OVERLAY_FRAME_POLICY";
    public const string OverlayResultMaxAgeMsKey = "ZLM_OVERLAY_RESULT_MAX_AGE_MS";
    public const string AnalysisSampleIntervalMsKey = "ZLM_ANALYSIS_SAMPLE_INTERVAL_MS";
    public const string AnalysisBusyPolicyKey = "ZLM_ANALYSIS_BUSY_POLICY";
    public const string AnalysisEnabledKey = "ZLM_ANALYSIS_ENABLED";
    public const string ExtraEnableKey = "ZLM_EXTRA_ENABLE";
    public const string OcrEnableKey = "ZLM_OCR_ENABLE";
    public const string OcrModelKey = "ZLM_OCR_MODEL";
    public const string OcrDetModelKey = "ZLM_OCR_DET_MODEL";
    public const string OcrRecModelKey = "ZLM_OCR_REC_MODEL";
    public const string OcrClsModelKey = "ZLM_OCR_CLS_MODEL";
    public const string OcrDictKey = "ZLM_OCR_DICT";
    public const string OcrMinScoreKey = "ZLM_OCR_MIN_SCORE";
    public const string OcrUseClsKey = "ZLM_OCR_USE_CLS";
    public const string OcrLineWorkersKey = "ZLM_OCR_LINE_WORKERS";
    public const string OcrSampleIntervalMsKey = "ZLM_OCR_SAMPLE_INTERVAL_MS";

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

    /// <summary>叠加流名后缀（<c>{stream}_ai</c>），自动录像等模块据此跳过派生流。</summary>
    public string OverlaySuffix { get; set; } = "_ai";

    /// <summary>
    /// 叠加取结果策略：<c>reuse</c>（默认）= 每帧都用最近一次分析结果绘制（不跳帧，标注可能滞后）；
    /// <c>skip</c> = 只绘制新鲜结果，来不及处理的帧不叠加（跳帧，画面无标注也不等待）。
    /// </summary>
    public string OverlayFramePolicy { get; set; } = "reuse";

    /// <summary>
    /// 分析结果允许复用的时长（毫秒）；超出后该结果不再绘制。0 = 按策略取默认值
    /// （reuse 5000、skip 200）。
    /// </summary>
    public int OverlayResultMaxAgeMs { get; set; }

    /// <summary>
    /// 分析器正忙（上一轮还没算完）时的策略：<c>skip</c>（默认）= 跳过本轮，不积压；
    /// <c>queue</c> = 排队等上一轮结束再处理（不跳帧，但延迟会累积）。
    /// </summary>
    public string AnalysisBusyPolicy { get; set; } = "skip";

    /// <summary>是否为「复用缓存结果」模式（非 skip 即复用）。</summary>
    public bool OverlayReuseResult =>
        !string.Equals(OverlayFramePolicy, "skip", StringComparison.OrdinalIgnoreCase);

    /// <summary>分析器忙时是否跳过本轮。</summary>
    public bool AnalysisSkipWhenBusy =>
        !string.Equals(AnalysisBusyPolicy, "queue", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析叠加结果的最长复用时长：显式配置优先，否则按策略取默认。</summary>
    public TimeSpan ResolveOverlayResultMaxAge()
    {
        var ms = OverlayResultMaxAgeMs > 0
            ? OverlayResultMaxAgeMs
            : OverlayReuseResult ? 5000 : 200;
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>分析调度的全局采样间隔（毫秒）；未单独配置时沿用 <c>ZLM_YOLO_SAMPLE_INTERVAL_MS</c>。</summary>
    public int AnalysisSampleIntervalMs { get; set; } = 1000;

    /// <summary>分析器白名单（逗号分隔，如 <c>yolo,ocr</c>）；空表示全部已注册分析器。</summary>
    public string AnalysisEnabled { get; set; } = string.Empty;

    /// <summary>是否启用 OCR 分析（默认关闭：CPU 占用明显，按需开启）。</summary>
    public bool OcrEnable { get; set; }

    /// <summary>内置模型名：<c>tiny</c>（默认，PP-OCRv6 tiny）。自定义模型路径齐全时此项被忽略。</summary>
    public string OcrModel { get; set; } = "tiny";

    /// <summary>自定义文本检测模型（.onnx）。与 REC/DICT 同时配置时优先于内置模型。</summary>
    public string OcrDetModel { get; set; } = string.Empty;

    /// <summary>自定义文字识别模型（.onnx）。</summary>
    public string OcrRecModel { get; set; } = string.Empty;

    /// <summary>可选方向分类模型（.onnx）。</summary>
    public string OcrClsModel { get; set; } = string.Empty;

    /// <summary>自定义字典文件（.txt，每行一个字符）。</summary>
    public string OcrDict { get; set; } = string.Empty;

    /// <summary>
    /// 识别分数下限（0~1），低于该值的文本行丢弃。
    /// PP-OCRv6 tiny 的分数普遍偏低（实测常见 0.3~0.5），默认 0.3 比较合适。
    /// </summary>
    public float OcrMinScore { get; set; } = 0.3f;

    /// <summary>是否用方向分类模型纠正倒置文本，默认开启。</summary>
    public bool OcrUseCls { get; set; } = true;

    /// <summary>文本行并行识别线程数，默认 1。</summary>
    public int OcrLineWorkers { get; set; } = 1;

    /// <summary>同一路流两次 OCR 的最小间隔（毫秒）。</summary>
    public int OcrSampleIntervalMs { get; set; } = 2000;

    /// <summary>是否配置了完整的一组自定义 OCR 模型。</summary>
    public bool OcrHasCustomModels =>
        OcrDetModel.Length > 0 && OcrRecModel.Length > 0 && OcrDict.Length > 0;

    /// <summary>按 exe 目录解析 OCR 模型/字典路径（绝对路径原样使用）；未配置返回空串。</summary>
    public string ResolveOcrPath(string path)
    {
        var text = path.Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return Path.IsPathRooted(text)
            ? Path.GetFullPath(text)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, text));
    }

    /// <summary>是否启用扩展包（分析调度与叠加）；<c>ZLM_EXTRA_ENABLE=0</c> 时关闭。</summary>
    public bool ExtraEnable { get; set; } = true;

    /// <summary>解析后的分析器白名单；空列表表示不过滤。</summary>
    public IReadOnlyList<string> AnalysisEnabledList =>
        AnalysisEnabled
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToLowerInvariant())
            .ToArray();

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
