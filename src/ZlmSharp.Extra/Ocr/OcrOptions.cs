using ZlmSharp.Configuration;

namespace ZlmSharp.Extra.Ocr;

/// <summary>OCR 推理参数（来自 <c>.env</c>；未启用时分析器按"未启用"处理）。</summary>
public sealed class OcrOptions
{
    /// <summary>内置模型名（当前仅 <c>tiny</c>，随 NuGet 模型包内置）。</summary>
    public const string DefaultModel = "tiny";

    public bool Enabled { get; init; }

    /// <summary>内置模型名；<see cref="UseCustomModels"/> 为 true 时忽略。</summary>
    public string Model { get; init; } = DefaultModel;

    public string DetModelPath { get; init; } = string.Empty;

    public string RecModelPath { get; init; } = string.Empty;

    public string ClsModelPath { get; init; } = string.Empty;

    public string DictPath { get; init; } = string.Empty;

    /// <summary>识别分数下限，低于该值的文本行丢弃（tiny 模型分数普遍偏低，默认 0.3）。</summary>
    public float MinScore { get; init; } = 0.3f;

    public bool UseDirectionClassification { get; init; } = true;

    public int LineWorkerCount { get; init; } = 1;

    /// <summary>同一路流两次 OCR 的最小间隔（毫秒）。</summary>
    public int SampleIntervalMs { get; init; } = 2000;

    /// <summary>检测 + 识别 + 字典齐全时使用自定义模型。</summary>
    public bool UseCustomModels =>
        DetModelPath.Length > 0 && RecModelPath.Length > 0 && DictPath.Length > 0;

    public static OcrOptions FromEnv(EnvOptions env)
    {
        ArgumentNullException.ThrowIfNull(env);

        return new OcrOptions
        {
            Enabled = env.OcrEnable,
            Model = env.OcrModel,
            DetModelPath = env.ResolveOcrPath(env.OcrDetModel),
            RecModelPath = env.ResolveOcrPath(env.OcrRecModel),
            ClsModelPath = env.ResolveOcrPath(env.OcrClsModel),
            DictPath = env.ResolveOcrPath(env.OcrDict),
            MinScore = env.OcrMinScore,
            UseDirectionClassification = env.OcrUseCls,
            LineWorkerCount = env.OcrLineWorkers,
            SampleIntervalMs = env.OcrSampleIntervalMs,
        };
    }
}
