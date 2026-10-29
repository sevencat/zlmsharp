namespace ZlmSharp.Ai;

/// <summary>喂给推理后端的参数（与具体后端实现无关）。</summary>
public sealed record YoloBackendOptions
{
    /// <summary><c>cpu</c> / <c>vulkan</c> / <c>metal</c> / <c>auto</c>，由后端按自身支持情况解释。</summary>
    public string Device { get; init; } = "cpu";

    public int InputWidth { get; init; } = 640;

    public int InputHeight { get; init; } = 640;

    public float Confidence { get; init; } = 0.25f;

    public int CpuThreads { get; init; }
}

/// <summary>后端返回的原始检测框（原图像素坐标，左上角 + 宽高）。</summary>
public readonly record struct YoloRawDetection(
    int ClassId,
    float Confidence,
    float X,
    float Y,
    float Width,
    float Height);

/// <summary>
/// YOLO 推理后端：一个模型对应一个实例。
/// 会话通常不支持并发推理，并发控制由 <see cref="YoloDetectorService"/> 统一负责。
/// </summary>
public interface IYoloInferenceBackend : IDisposable
{
    /// <summary>后端名（如 <c>simdyolo</c>），仅用于日志。</summary>
    string Name { get; }

    /// <summary>类别 id → 类别名。</summary>
    IReadOnlyDictionary<int, string> ClassNames { get; }

    /// <summary>
    /// 同步推理一帧；<paramref name="confidence"/> 为 null 时用模型自带阈值。
    /// </summary>
    IReadOnlyList<YoloRawDetection> Detect(RgbImage image, float? confidence);
}

/// <summary>
/// 推理后端工厂：按模型路径创建 <see cref="IYoloInferenceBackend"/>，
/// 并在不加载权重的前提下读取模型元信息。
/// </summary>
/// <remarks>
/// 具体实现（如 SimdYolo 适配器）应放在单独项目里，通过 DI 覆盖
/// <see cref="UnavailableYoloBackendFactory"/> 的注册；核心库不直接依赖任何推理引擎。
/// </remarks>
public interface IYoloBackendFactory
{
    string Name { get; }

    /// <summary>当前构建/运行环境是否具备该后端；为 false 时检测相关入口直接按"未启用"处理。</summary>
    bool IsAvailable { get; }

    IYoloInferenceBackend Load(string modelPath, YoloBackendOptions options);

    /// <summary>只读模型文件头拿元信息；读不到返回 null。</summary>
    YoloModelSummary? ReadModelInfo(string modelPath);
}

/// <summary>
/// 未接入推理后端时的占位实现：<see cref="IsAvailable"/> 为 false，加载一律抛
/// <see cref="NotSupportedException"/>。
/// </summary>
public sealed class UnavailableYoloBackendFactory : IYoloBackendFactory
{
    public static UnavailableYoloBackendFactory Instance { get; } = new();

    private UnavailableYoloBackendFactory()
    {
    }

    public string Name => "unavailable";

    public bool IsAvailable => false;

    public IYoloInferenceBackend Load(string modelPath, YoloBackendOptions options) =>
        throw new NotSupportedException("当前构建未包含 YOLO 推理后端（推理引擎适配器待接入）");

    public YoloModelSummary? ReadModelInfo(string modelPath) => null;
}
