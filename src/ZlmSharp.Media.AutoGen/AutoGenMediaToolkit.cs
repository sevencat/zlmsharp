using ZlmSharp.Ai;
using ZlmSharp.Extra.Media;

namespace ZlmSharp.Media.AutoGen;

/// <summary>
/// 基于 FFmpeg.AutoGen 8.x 的 <see cref="IMediaToolkit"/> 实现：直接 P/Invoke 同目录的
/// avcodec-62 / avformat-62 / avutil-60 / swscale-9 等 dll，不启动 ffmpeg.exe 子进程、不走管道。
/// </summary>
/// <remarks>
/// <para>dll 目录由构造参数决定（<c>ffmpeg.exe</c> 的路径或 dll 所在目录），
/// 与 <c>ffmpeg.exe</c> 放在同一目录即可。</para>
/// <para>能力与 <see cref="FfmpegToolkit"/> 对齐：抓单帧、常驻采样解码、顺序解码、libx264 推流编码。
/// 不依赖 ffprobe，<see cref="FfprobePath"/> 返回空串。</para>
/// </remarks>
public sealed class AutoGenMediaToolkit : IMediaToolkit
{
    private static readonly TimeSpan DefaultGrabTimeout = TimeSpan.FromSeconds(10);

    /// <param name="ffmpegPathOrDirectory">
    /// <c>ffmpeg.exe</c> 的路径（取其所在目录）或 dll 目录；为空则用当前程序目录。
    /// </param>
    /// <exception cref="InvalidOperationException">dll 目录缺少 ffmpeg 8.x 动态库。</exception>
    public AutoGenMediaToolkit(string? ffmpegPathOrDirectory = null)
    {
        FfmpegPath = LibavEnvironment.ResolveDirectory(ffmpegPathOrDirectory);
        LibavEnvironment.Initialize(FfmpegPath);
    }

    /// <summary>实际使用的 dll 目录（不是 exe 路径）。</summary>
    public string FfmpegPath { get; }

    /// <summary>libav 不提供 ffprobe 能力。</summary>
    public string FfprobePath => string.Empty;

    /// <summary>libav 版本（如 <c>8.0.git</c>），便于日志排查。</summary>
    public string VersionInfo => LibavEnvironment.VersionInfo();

    public async Task<RgbImage> GrabFrameAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var limit = timeout > TimeSpan.Zero ? timeout : DefaultGrabTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(limit);

        var work = Task.Run(
            () =>
            {
                using var session = LibavDecoderSession.Open(url, 0, 0);
                var width = session.Width;
                var height = session.Height;
                var pixels = new byte[checked(width * height * 3)];

                if (!session.TryReadFrame(pixels, cts.Token))
                {
                    throw new InvalidOperationException($"未取到视频帧：{url}");
                }

                return new RgbImage(pixels, width, height);
            },
            CancellationToken.None);

        try
        {
            return await work.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"抓帧超时（{limit.TotalSeconds:0.#}s）：{url}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"抓帧超时（{limit.TotalSeconds:0.#}s）：{url}");
        }
    }

    public IFrameSampler OpenSampler(string url, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        return new AutoGenFrameSampler(url, width, height);
    }

    public IFrameReader OpenReader(string url, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        return new AutoGenFrameReader(url, width, height);
    }

    public IFrameSink OpenSink(FrameSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputUrl);

        return new AutoGenFrameSink(options);
    }
}
