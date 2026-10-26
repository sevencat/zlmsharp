using System.Diagnostics;

namespace ZlmSharp.Ai;

/// <summary>
/// 常驻的 ffmpeg 解码子进程：一条流一个进程，持续输出固定尺寸的 RGB24 原始帧，
/// 只保留「最新一帧」供采样方取用（旧帧直接丢弃，不会累积延迟）。
/// 用于替代每次采样都起一次 ffmpeg 的旧做法。
/// </summary>
public sealed class FfmpegLiveDecoder : IAsyncDisposable
{
    private readonly Process _process;
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameSize;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _frameReady = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;
    private readonly Task _stderrTask;

    private byte[] _latest;
    private int _disposed;

    private FfmpegLiveDecoder(Process process, int width, int height)
    {
        _process = process;
        _width = width;
        _height = height;
        _frameSize = checked(width * height * 3);
        _latest = new byte[_frameSize];

        _stderrTask = Task.Run(() => ReadStderrAsync(_cts.Token));
        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    public int Width => _width;

    public int Height => _height;

    public bool IsRunning => Volatile.Read(ref _disposed) == 0 && !_process.HasExited;

    public string LastError { get; private set; } = string.Empty;

    /// <summary>启动解码器；<paramref name="url"/> 支持 RTSP/RTMP/HTTP-FLV。</summary>
    /// <exception cref="InvalidOperationException">ffmpeg 无法启动时抛出。</exception>
    public static FfmpegLiveDecoder Start(string ffmpegPath, string url, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in BuildArguments(url, width, height))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            process.Dispose();
            throw new InvalidOperationException($"无法启动 ffmpeg（{startInfo.FileName}）：{exception.Message}", exception);
        }

        return new FfmpegLiveDecoder(process, width, height);
    }

    /// <summary>取最新一帧；超时或进程已退出返回 null。</summary>
    public async Task<RgbImage?> ReadFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await _frameReady.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        // 复制出去：pump 会立刻复用读缓冲，不能把内部数组交给调用方
        var pixels = new byte[_frameSize];
        lock (_gate)
        {
            Buffer.BlockCopy(_latest, 0, pixels, 0, _frameSize);
        }

        return new RgbImage(pixels, _width, _height);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 进程可能刚好自行退出
        }

        foreach (var task in new[] { _pumpTask, _stderrTask })
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _process.Dispose();
        _frameReady.Dispose();
        _cts.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var stdout = _process.StandardOutput.BaseStream;
        var buffer = new byte[_frameSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = 0;
                while (read < _frameSize)
                {
                    var chunk = await stdout
                        .ReadAsync(buffer.AsMemory(read, _frameSize - read), cancellationToken)
                        .ConfigureAwait(false);

                    if (chunk <= 0)
                    {
                        return;
                    }

                    read += chunk;
                }

                lock (_gate)
                {
                    (_latest, buffer) = (buffer, _latest);
                }

                // buffer 换成了上一帧的数组，可继续复用
                try
                {
                    _frameReady.Release();
                }
                catch (SemaphoreFullException)
                {
                    // 已有未取走的帧：读者会拿到最新内容，无需堆积信号
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            LastError = exception.Message;
        }
    }

    private async Task ReadStderrAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stderr = _process.StandardError;
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await stderr.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (line.Length > 0)
                {
                    LastError = line.Length <= 400 ? line : line[..400];
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static IEnumerable<string> BuildArguments(string url, int width, int height)
    {
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "error";
        yield return "-nostdin";

        // 注意：不要加 -fflags nobuffer / -flags low_delay。
        // 实测这两项会让解码器只出一帧甚至零帧（尤其对文件输入）；
        // 「不累积延迟」由读循环持续丢弃旧帧保证，不靠 ffmpeg 的输入缓冲开关。

        if (url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            yield return "-rtsp_transport";
            yield return "tcp";
        }

        yield return "-i";
        yield return url;
        yield return "-an";
        yield return "-sn";
        yield return "-vf";
        yield return $"scale={width}:{height}";
        yield return "-pix_fmt";
        yield return "rgb24";
        yield return "-f";
        yield return "rawvideo";
        yield return "pipe:1";
    }
}
