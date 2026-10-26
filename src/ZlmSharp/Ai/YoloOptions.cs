using ZlmSharp.Configuration;

namespace ZlmSharp.Ai;

/// <summary>YOLO 推理参数（来自 <c>.env</c>，模型路径为空即关闭）。</summary>
public sealed class YoloOptions
{
    /// <summary>默认模型绝对路径；空串表示未指定默认模型。</summary>
    public string ModelPath { get; init; } = string.Empty;

    /// <summary>模型目录绝对路径（用于按文件名选择模型、列出可选模型）。</summary>
    public string ModelDir { get; init; } = string.Empty;

    /// <summary><c>cpu</c> / <c>vulkan</c> / <c>metal</c> / <c>auto</c>。</summary>
    public string Device { get; init; } = "cpu";

    public float Confidence { get; init; } = 0.25f;

    public int InputWidth { get; init; } = 640;

    public int InputHeight { get; init; } = 640;

    public int CpuThreads { get; init; }

    /// <summary>同一路流两次检测的最小间隔（毫秒），0 表示不限速。</summary>
    public int SampleIntervalMs { get; init; } = 1000;

    /// <summary>同时缓存的模型数量（每个模型各持一份权重）。</summary>
    public int MaxCachedModels { get; init; } = 2;

    public bool Enabled => ModelPath.Length > 0;

    public static YoloOptions FromEnv(EnvOptions env) => new()
    {
        ModelPath = env.ResolveYoloModelPath(),
        ModelDir = env.ResolveYoloModelDir(),
        Device = env.YoloDevice,
        Confidence = env.YoloConfidence,
        InputWidth = env.YoloInputWidth,
        InputHeight = env.YoloInputHeight,
        CpuThreads = env.YoloCpuThreads,
        SampleIntervalMs = env.YoloSampleIntervalMs,
    };
}
