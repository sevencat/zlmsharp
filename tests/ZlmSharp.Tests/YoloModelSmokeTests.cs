using ZlmSharp.Ai;

namespace ZlmSharp.Tests;

/// <summary>
/// 真实模型冒烟：默认跳过（CI 没有模型文件），
/// 设 <c>ZLM_YOLO_TEST_MODEL</c> 指向本地 .pt/.onnx 时运行，验证 加载 → 推理 → 结果映射 全链路。
/// </summary>
public class YoloModelSmokeTests
{
    [Fact]
    public async Task Detect_RunsOnRealModel_WhenConfigured()
    {
        var modelPath = Environment.GetEnvironmentVariable("ZLM_YOLO_TEST_MODEL");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            return;
        }

        using var service = new YoloDetectorService(new YoloOptions
        {
            ModelPath = modelPath,
            Device = "cpu",
            InputWidth = 640,
            InputHeight = 640,
            Confidence = 0.25f,
        });

        if (!service.BackendAvailable)
        {
            // 当前构建未接入推理后端（核心库只定义 IYoloBackendFactory）
            return;
        }

        var summary = service.ReadModelSummary();
        Assert.NotNull(summary);
        Assert.True(summary!.ClassCount > 0);
        Assert.NotEmpty(summary.ClassNames);

        // 纯色图：只验证加载与推理链路跑通，检测结果允许为空
        const int width = 640;
        const int height = 480;
        var pixels = new byte[width * height * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 32;
            pixels[i + 1] = 64;
            pixels[i + 2] = 128;
        }

        var result = await service.DetectAsync(new RgbImage(pixels, width, height));

        Assert.True(service.Loaded);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.NotNull(result.Objects);
        Assert.True(result.ElapsedMs >= 0);
    }
}
