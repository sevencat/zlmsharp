using System.Text;
using ZlmSharp.Ai;
using ZlmSharp.Configuration;

namespace ZlmSharp.Tests;

public class YoloTests
{
    [Fact]
    public void Ppm_ParsesRgb24()
    {
        var data = BuildPpm(2, 2, out var pixels);

        Assert.True(PpmImage.TryParse(data, out var image, out var error), error);
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(6, image.Stride);
        Assert.Equal(pixels, image.Pixels);
    }

    [Fact]
    public void Ppm_SkipsCommentsAndBlankLines()
    {
        var header = Encoding.ASCII.GetBytes("P6\n# 注释\n\n2 1\n# 又一行注释\n255\r\n");
        var data = new byte[header.Length + 6];
        header.CopyTo(data, 0);
        new byte[] { 1, 2, 3, 4, 5, 6 }.CopyTo(data, header.Length);

        Assert.True(PpmImage.TryParse(data, out var image, out var error), error);
        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, image.Pixels);
    }

    [Fact]
    public void Ppm_RejectsUnsupportedOrTruncated()
    {
        Assert.False(PpmImage.TryParse("P3\n1 1\n255\n0 0 0"u8.ToArray(), out _, out var magicError));
        Assert.Contains("P6", magicError);

        var header = Encoding.ASCII.GetBytes("P6\n2 2\n255\n");
        var truncated = new byte[header.Length + 6];
        header.CopyTo(truncated, 0);
        Assert.False(PpmImage.TryParse(truncated, out _, out var sizeError));
        Assert.Contains("不足", sizeError);

        Assert.False(PpmImage.TryParse("P6\n1 1\n65535\n\0\0\0"u8.ToArray(), out _, out var maxError));
        Assert.Contains("255", maxError);
    }

    [Fact]
    public void RgbImage_ValidatesStrideAndSize()
    {
        Assert.Throws<ArgumentException>(() => new RgbImage(new byte[5], 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RgbImage(new byte[6], 2, 1, stride: 5));

        var image = new RgbImage(new byte[8], 2, 1, stride: 8);
        Assert.Equal(8, image.Stride);
    }

    [Fact]
    public async Task Detector_Disabled_Throws()
    {
        using var service = new YoloDetectorService(new YoloOptions());
        Assert.False(service.Enabled);
        Assert.Null(service.ReadModelSummary());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DetectAsync(new RgbImage(new byte[3], 1, 1)));
    }

    [Fact]
    public void Options_FromEnv_ResolvesModelPath()
    {
        var env = new EnvOptions
        {
            YoloModel = "models/yolo11n.onnx",
            YoloDevice = "vulkan",
            YoloConfidence = 0.5f,
            YoloInputWidth = 320,
            YoloInputHeight = 320,
        };

        var options = YoloOptions.FromEnv(env);

        Assert.True(options.Enabled);
        Assert.True(Path.IsPathRooted(options.ModelPath));
        Assert.EndsWith("yolo11n.onnx", options.ModelPath);
        Assert.Equal("vulkan", options.Device);
        Assert.Equal(0.5f, options.Confidence);
        Assert.Equal(320, options.InputWidth);
    }

    [Fact]
    public void Options_BlankModel_IsDisabled()
    {
        var options = YoloOptions.FromEnv(new EnvOptions { YoloModel = "  " });
        Assert.False(options.Enabled);
        Assert.Equal(string.Empty, options.ModelPath);
    }

    [Fact]
    public void Options_ModelDir_ResolvesBareFileName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zlm-yolo-models");
        var options = YoloOptions.FromEnv(new EnvOptions
        {
            YoloModelDir = dir,
            YoloModel = "yolov8s.onnx",
        });

        Assert.True(options.Enabled);
        Assert.Equal(Path.Combine(dir, "yolov8s.onnx"), options.ModelPath);
        Assert.Equal(dir, options.ModelDir);

        // 绝对路径优先于模型目录
        var absolute = Path.Combine(Path.GetTempPath(), "other", "yolo11n.onnx");
        var absoluteOptions = YoloOptions.FromEnv(new EnvOptions { YoloModelDir = dir, YoloModel = absolute });
        Assert.Equal(absolute, absoluteOptions.ModelPath);
    }

    [Fact]
    public void ResolveModelPath_ValidatesModelName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zlm-yolo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var modelFile = Path.Combine(dir, "dummy.onnx");
            File.WriteAllBytes(modelFile, [0x00]);

            using var service = new YoloDetectorService(new YoloOptions
            {
                ModelDir = dir,
                ModelPath = modelFile,
            });

            Assert.Equal(modelFile, service.ResolveModelPath());
            Assert.Equal(modelFile, service.ResolveModelPath("dummy.onnx"));
            Assert.Equal(["dummy.onnx"], service.AvailableModels);
            Assert.False(service.Loaded);

            Assert.Throws<ArgumentException>(() => service.ResolveModelPath("../dummy.onnx"));
            Assert.Throws<ArgumentException>(() => service.ResolveModelPath("dummy.txt"));
            Assert.Throws<FileNotFoundException>(() => service.ResolveModelPath("missing.onnx"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveModelPath_WithoutModelDir_RejectsName()
    {
        using var service = new YoloDetectorService(new YoloOptions { ModelPath = "x.onnx" });
        Assert.Throws<InvalidOperationException>(() => service.ResolveModelPath("other.onnx"));
    }

    private static byte[] BuildPpm(int width, int height, out byte[] pixels)
    {
        pixels = new byte[width * height * 3];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)i;
        }

        var header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        var data = new byte[header.Length + pixels.Length];
        header.CopyTo(data, 0);
        pixels.CopyTo(data, header.Length);
        return data;
    }
}
