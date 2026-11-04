using System.Diagnostics;
using System.Globalization;
using ZlmSharp.Ai;
using ZlmSharp.Extra.Capture;

namespace ZlmSharp.Extra.Media;

/// <summary>
/// <see cref="IMediaToolkit"/> 的进程实现：用 ffmpeg 子进程 + 标准流管道完成抽帧、解码、编码。
/// 所有命令行细节都收敛在这里；将来换成进程内 libav 实现时，只要替换本类即可。
/// </summary>
public sealed class FfmpegToolkit : IMediaToolkit
{
    private static readonly TimeSpan DefaultGrabTimeout = TimeSpan.FromSeconds(10);

    public FfmpegToolkit(string? ffmpegPath = null, string? ffprobePath = null)
    {
        FfmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();
        FfprobePath = string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath.Trim();
    }

    public string FfmpegPath { get; }

    public string FfprobePath { get; }

    public async Task<RgbImage> GrabFrameAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var limit = timeout > TimeSpan.Zero ? timeout : DefaultGrabTimeout;
        using var process = StartProcess(BuildGrabArguments(url), tag: "grab", redirectInput: false, redirectOutput: true);

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
            Kill(process);
        }

        if (!PpmImage.TryParse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), out var image, out var error))
        {
            throw new InvalidDataException($"ffmpeg 输出不是合法 PPM：{error}");
        }

        return image;
    }

    public IFrameSampler OpenSampler(string url, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var process = StartProcess(
            BuildDecodeArguments(url, width, height),
            tag: "decoder",
            redirectInput: false,
            redirectOutput: true);

        return new ProcessFrameSampler(process, width, height);
    }

    public IFrameReader OpenReader(string url, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var process = StartProcess(
            BuildDecodeArguments(url, width, height),
            tag: "decoder",
            redirectInput: false,
            redirectOutput: true);

        return new ProcessFrameReader(process, width, height);
    }

    public IFrameSink OpenSink(FrameSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputUrl);

        var process = StartProcess(
            BuildEncodeArguments(options),
            tag: "encoder",
            redirectInput: true,
            redirectOutput: false);

        return new ProcessFrameSink(process, options.Width * options.Height * 3);
    }

    /// <summary>启动 ffmpeg 子进程并挂上 stderr 采集；启动失败抛 <see cref="InvalidOperationException"/>。</summary>
    private Process StartProcess(
        IEnumerable<string> arguments,
        string tag,
        bool redirectInput,
        bool redirectOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
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
            throw new InvalidOperationException($"无法启动 ffmpeg（{FfmpegPath}）：{exception.Message}", exception);
        }

        _ = Task.Run(() => DrainStderrAsync(process, tag));
        return process;
    }

    private static IEnumerable<string> BuildGrabArguments(string url)
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

    private static IEnumerable<string> BuildDecodeArguments(string url, int width, int height)
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
        yield return string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height}");
        yield return "-pix_fmt";
        yield return "rgb24";
        yield return "-f";
        yield return "rawvideo";
        yield return "pipe:1";
    }

    private static IEnumerable<string> BuildEncodeArguments(FrameSinkOptions options)
    {
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "error";
        yield return "-nostdin";
        yield return "-f";
        yield return "rawvideo";
        yield return "-pixel_format";
        yield return "rgb24";
        yield return "-video_size";
        yield return string.Create(CultureInfo.InvariantCulture, $"{options.Width}x{options.Height}");
        yield return "-framerate";
        yield return options.Fps.ToString(CultureInfo.InvariantCulture);
        yield return "-i";
        yield return "pipe:0";
        yield return "-an";
        yield return "-c:v";
        yield return "libx264";
        yield return "-preset";
        yield return "veryfast";
        yield return "-tune";
        yield return "zerolatency";
        yield return "-pix_fmt";
        yield return "yuv420p";
        yield return "-g";
        yield return options.GopSize.ToString(CultureInfo.InvariantCulture);
        yield return "-keyint_min";
        yield return options.Fps.ToString(CultureInfo.InvariantCulture);
        yield return "-sc_threshold";
        yield return "0";
        yield return "-b:v";
        yield return $"{options.BitrateKbps}k";
        yield return "-maxrate";
        yield return $"{options.BitrateKbps}k";
        yield return "-bufsize";
        yield return $"{options.BitrateKbps * 2}k";
        yield return "-f";
        yield return "flv";
        yield return options.OutputUrl;
    }

    private static async Task DrainStderrAsync(Process process, string tag)
    {
        try
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }
            }
        }
        catch (Exception)
        {
            // 进程被杀时读 stderr 会抛异常，忽略；tag 仅用于调试定位
            _ = tag;
        }
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

    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }
    }

    internal static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>只保留最新一帧的常驻解码读循环（分析采样用）。</summary>
internal sealed class ProcessFrameSampler : IFrameSampler
{
    private readonly Process _process;
    private readonly int _frameSize;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _frameReady = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;
    private readonly Task _stderrTask;

    private byte[] _latest;
    private int _disposed;

    public ProcessFrameSampler(Process process, int width, int height)
    {
        _process = process;
        Width = width;
        Height = height;
        _frameSize = checked(width * height * 3);
        _latest = new byte[_frameSize];

        _stderrTask = Task.Run(() => ReadStderrAsync(_cts.Token));
        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    public int Width { get; }

    public int Height { get; }

    public bool IsRunning => Volatile.Read(ref _disposed) == 0 && !_process.HasExited;

    public string LastError { get; private set; } = string.Empty;

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

        return new RgbImage(pixels, Width, Height);
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

        FfmpegToolkit.Kill(_process);

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
}

/// <summary>顺序读取每一帧的解码视图（转码/叠加用，不丢帧）。</summary>
internal sealed class ProcessFrameReader : IFrameReader
{
    private readonly Process _process;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _stderrTask;
    private int _disposed;

    public ProcessFrameReader(Process process, int width, int height)
    {
        _process = process;
        Width = width;
        Height = height;
        _stderrTask = Task.Run(() => ReadStderrAsync(_cts.Token));
    }

    public int Width { get; }

    public int Height { get; }

    public bool IsRunning => Volatile.Read(ref _disposed) == 0 && !_process.HasExited;

    public int? ExitCode => FfmpegToolkit.TryGetExitCode(_process);

    public string LastError { get; private set; } = string.Empty;

    public async Task<bool> ReadIntoAsync(Memory<byte> frame, CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            return false;
        }

        var stdout = _process.StandardOutput.BaseStream;
        var read = 0;

        try
        {
            while (read < frame.Length)
            {
                var chunk = await stdout.ReadAsync(frame[read..], cancellationToken).ConfigureAwait(false);
                if (chunk <= 0)
                {
                    return false;
                }

                read += chunk;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            LastError = exception.Message;
            return false;
        }
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

        FfmpegToolkit.Kill(_process);

        try
        {
            await _stderrTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _process.Dispose();
        _cts.Dispose();
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
}

/// <summary>接收 RGB24 原始帧的编码进程（叠加流转码输出）。</summary>
internal sealed class ProcessFrameSink : IFrameSink
{
    private readonly Process _process;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _stderrTask;
    private int _disposed;

    public ProcessFrameSink(Process process, int frameSize)
    {
        _process = process;
        FrameSize = frameSize;
        _stderrTask = Task.Run(() => ReadStderrAsync(_cts.Token));
    }

    public int FrameSize { get; }

    public bool IsRunning => Volatile.Read(ref _disposed) == 0 && !_process.HasExited;

    public int? ExitCode => FfmpegToolkit.TryGetExitCode(_process);

    public string LastError { get; private set; } = string.Empty;

    public async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        var stdin = _process.StandardInput.BaseStream;
        await stdin.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int?> CompleteAsync(TimeSpan timeout)
    {
        // 关闭 stdin 让 ffmpeg 写完 trailer（flv/mp4 收尾）后自行退出
        try
        {
            _process.StandardInput.BaseStream.Close();
        }
        catch (Exception)
        {
        }

        return await WaitForExitAsync(timeout).ConfigureAwait(false);
    }

    public async Task<int?> WaitForExitAsync(TimeSpan timeout)
    {
        try
        {
            if (!_process.HasExited)
            {
                using var cts = new CancellationTokenSource(timeout);
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            FfmpegToolkit.Kill(_process);
        }

        return FfmpegToolkit.TryGetExitCode(_process);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _process.StandardInput.BaseStream.Close();
        }
        catch (Exception)
        {
        }

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        FfmpegToolkit.Kill(_process);

        try
        {
            await _stderrTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _process.Dispose();
        _cts.Dispose();
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
}
