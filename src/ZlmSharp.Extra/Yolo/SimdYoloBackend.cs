using ZlmSharp.Ai;

namespace ZlmSharp.Extra.Yolo;

/// <summary>
/// SimdYolo 推理后端工厂：纯托管 C#，CPU 走 SIMD、可选 Vulkan / Metal。
/// </summary>
/// <remarks>
/// 源码位于仓库根的 <c>/simdyolo</c>（不随仓库分发）。构建时该目录存在则编译 <c>SIMDYOLO</c> 常量并接入；
/// 不存在时本工厂的 <see cref="IsAvailable"/> 为 false，检测入口按"未启用"处理，其余功能不受影响。
/// </remarks>
public sealed class SimdYoloBackendFactory : IYoloBackendFactory
{
    public const string BackendName = "simdyolo";

    public string Name => BackendName;

#if SIMDYOLO
    public bool IsAvailable => true;

    public IYoloInferenceBackend Load(string modelPath, YoloBackendOptions options) =>
        new SimdYoloBackend(modelPath, options);

    public YoloModelSummary? ReadModelInfo(string modelPath)
    {
        try
        {
            // 只读模型头，不初始化计算后端
            var info = global::SimdYolo.ModelInfo.Read(modelPath);
            var names = info.ClassNames
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .ToArray();
            return new YoloModelSummary(info.Format, info.Task.ToString(), info.ClassCount, names);
        }
        catch (Exception)
        {
            return null;
        }
    }
#else
    public bool IsAvailable => false;

    public IYoloInferenceBackend Load(string modelPath, YoloBackendOptions options) =>
        throw new NotSupportedException(
            "当前构建未包含 SimdYolo：请把 SimdYolo 源码放到仓库根的 simdyolo/ 目录后重新构建");

    public YoloModelSummary? ReadModelInfo(string modelPath) => null;
#endif
}

#if SIMDYOLO
/// <summary>SimdYolo 的一个模型会话（检测/分割/姿态共用同一入口，这里取检测框）。</summary>
internal sealed class SimdYoloBackend : IYoloInferenceBackend
{
    private readonly global::SimdYolo.YoloDetector _detector;
    private readonly float _defaultConfidence;

    public SimdYoloBackend(string modelPath, YoloBackendOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(options);

        _defaultConfidence = options.Confidence;
        _detector = new global::SimdYolo.YoloDetector(
            modelPath,
            new global::SimdYolo.DetectorOptions
            {
                Device = ParseDevice(options.Device),
                Width = options.InputWidth,
                Height = options.InputHeight,
                Confidence = options.Confidence,
                CpuThreads = Math.Max(0, options.CpuThreads),
            });

        ClassNames = _detector.Info.ClassNames;
    }

    public string Name => SimdYoloBackendFactory.BackendName;

    public IReadOnlyDictionary<int, string> ClassNames { get; }

    public IReadOnlyList<YoloRawDetection> Detect(RgbImage image, float? confidence)
    {
        ArgumentNullException.ThrowIfNull(image);

        // RgbImage 是 RGB24（可带行跨度），与 SimdYolo 的默认输入布局一致
        var instances = _detector.Predict(
            image.Pixels,
            image.Width,
            image.Height,
            confidence ?? _defaultConfidence,
            bgr: false,
            rowStride: image.Stride);

        if (instances.Count == 0)
        {
            return [];
        }

        var detections = new YoloRawDetection[instances.Count];
        for (var index = 0; index < instances.Count; index++)
        {
            var box = instances[index].Box;
            detections[index] = new YoloRawDetection(
                box.ClassId,
                box.Confidence,
                box.X,
                box.Y,
                box.Width,
                box.Height);
        }

        return detections;
    }

    public void Dispose() => _detector.Dispose();

    private static global::SimdYolo.ComputeDevice ParseDevice(string? device) =>
        device?.Trim().ToLowerInvariant() switch
        {
            "vulkan" or "gpu" => global::SimdYolo.ComputeDevice.Vulkan,
            "metal" => global::SimdYolo.ComputeDevice.Metal,
            "auto" => global::SimdYolo.ComputeDevice.Auto,
            _ => global::SimdYolo.ComputeDevice.Cpu,
        };
}
#endif
