using System.Diagnostics;
using ZlmSharp.Ai;
using ZlmSharp.Core;

namespace ZlmSharp.Tests;

/// <summary>
/// 常驻解码器：不依赖 ffmpeg 的启动失败路径，以及需要本机 ffmpeg 的真实解码冒烟。
/// 没有 ffmpeg 时（CI）真实解码用例直接跳过（返回）。
/// </summary>
public class FfmpegLiveDecoderTests
{
    [Fact]
    public void Start_WithMissingBinary_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-ffmpeg-{Guid.NewGuid():N}.exe");

        var exception = Assert.Throws<InvalidOperationException>(
            () => FfmpegLiveDecoder.Start(missing, "rtsp://127.0.0.1/x", 320, 240));

        Assert.Contains("ffmpeg", exception.Message);
    }

    [Fact]
    public async Task Pool_DecodesLocalVideo_AndReleasesOnRemove()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            return; // 本机没有 ffmpeg
        }

        var videoPath = Path.Combine(Path.GetTempPath(), $"zlmsharp-decode-{Guid.NewGuid():N}.mp4");
        try
        {
            Assert.True(await CreateTestVideoAsync(ffmpeg, videoPath), "生成测试视频失败");

            var key = StreamKey.Create("__defaultVhost__", "live", "decoder-test");
            await using var pool = new FfmpegDecoderPool(ffmpeg);

            var image = await pool.ReadFrameAsync(
                key,
                videoPath,
                320,
                240,
                TimeSpan.FromSeconds(15),
                CancellationToken.None);

            Assert.NotNull(image);
            Assert.Equal(320, image!.Width);
            Assert.Equal(240, image.Height);
            Assert.Equal(320 * 240 * 3, image.Pixels.Length);
            Assert.Contains(image.Pixels, value => value != 0);

            Assert.Equal(1, pool.Count);
            pool.Remove(key);
            Assert.Equal(0, pool.Count);
        }
        finally
        {
            try
            {
                File.Delete(videoPath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Decoder_ReturnsLatestFrame_WithoutAccumulating()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            return;
        }

        var videoPath = Path.Combine(Path.GetTempPath(), $"zlmsharp-frames-{Guid.NewGuid():N}.mp4");
        try
        {
            Assert.True(await CreateTestVideoAsync(ffmpeg, videoPath), "生成测试视频失败");

            await using var decoder = FfmpegLiveDecoder.Start(ffmpeg, videoPath, 160, 120);
            var first = await decoder.ReadFrameAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
            Assert.NotNull(first);

            // 连续取帧不应该抛出，且每次都是完整帧
            for (var index = 0; index < 3; index++)
            {
                var next = await decoder.ReadFrameAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                if (next is null)
                {
                    break; // 视频已 EOF，允许提前结束
                }

                Assert.Equal(160, next.Width);
                Assert.Equal(120, next.Height);
                Assert.Equal(160 * 120 * 3, next.Pixels.Length);
            }
        }
        finally
        {
            try
            {
                File.Delete(videoPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<bool> CreateTestVideoAsync(string ffmpeg, string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
                 {
                     "-y", "-hide_banner", "-loglevel", "error",
                     "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=10",
                     "-pix_fmt", "yuv420p",
                     path,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        _ = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0;
    }

    /// <summary>取本机 ffmpeg（优先 <c>ZLM_FFMPEG_TEST_PATH</c>，否则 PATH 里的 ffmpeg）；没有返回 null。</summary>
    private static string? FindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("ZLM_FFMPEG_TEST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-version");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            return process.ExitCode == 0 ? "ffmpeg" : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
