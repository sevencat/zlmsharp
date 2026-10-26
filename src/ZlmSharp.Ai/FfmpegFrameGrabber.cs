using System.Diagnostics;

namespace ZlmSharp.Ai;

/// <summary>
/// 用 ffmpeg 从任意 URL（RTSP/RTMP/HTTP-FLV 等）抓一帧并转成 RGB24（PPM），
/// 作为「直播流 → YOLO」的取帧通道（本项目没有内置 H.264 解码器）。
/// </summary>
public static class FfmpegFrameGrabber
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static async Task<RgbImage> GrabAsync(
        string ffmpegPath,
        string url,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var limit = timeout is { } value && value > TimeSpan.Zero ? value : DefaultTimeout;
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in BuildArguments(url))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"无法启动 ffmpeg（{startInfo.FileName}）：{exception.Message}", exception);
        }

        using var buffer = new MemoryStream();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(limit);

        try
        {
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(buffer, timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            var errorText = await stderr.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg 抽帧失败（exit={process.ExitCode}）：{Summarize(errorText)}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ffmpeg 抽帧超时（{limit.TotalSeconds:F0}s）：{url}");
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        if (!PpmImage.TryParse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), out var image, out var error))
        {
            throw new InvalidDataException($"ffmpeg 输出不是合法 PPM：{error}");
        }

        return image;
    }

    private static IEnumerable<string> BuildArguments(string url)
    {
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "error";
        yield return "-nostdin";

        if (url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            // 抓单帧走 TCP interleaved，避免 UDP 丢包/防火墙导致的偶发超时
            yield return "-rtsp_transport";
            yield return "tcp";
        }

        yield return "-i";
        yield return url;
        yield return "-frames:v";
        yield return "1";
        yield return "-an";
        yield return "-sn";
        yield return "-f";
        yield return "image2pipe";
        yield return "-vcodec";
        yield return "ppm";
        yield return "pipe:1";
    }

    private static string Summarize(string stderr)
    {
        var text = stderr.Trim();
        if (text.Length == 0)
        {
            return "无 stderr 输出";
        }

        var line = text.Split('\n')[^1].Trim();
        return line.Length <= 400 ? line : line[..400];
    }
}
