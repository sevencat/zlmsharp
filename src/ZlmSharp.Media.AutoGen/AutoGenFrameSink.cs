using System.Collections.Concurrent;
using System.Threading.Channels;
using ZlmSharp.Extra.Media;

namespace ZlmSharp.Media.AutoGen;

/// <summary>
/// 进程内编码：RGB24 帧写入有界队列 → 后台编码线程（libx264 → flv/rtmp）。
/// 与 exe 实现的 <c>ProcessFrameSink</c> 语义一致；<see cref="IFrameSink.ExitCode"/> 0 = 正常收尾，1 = 出错。
/// </summary>
internal sealed class AutoGenFrameSink : IFrameSink
{
    private readonly FrameSinkOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _input;
    private readonly ConcurrentBag<byte[]> _pool = new();
    private readonly int _frameSize;
    private readonly Task _encodeTask;
    private volatile bool _running = true;
    private volatile bool _finished;
    private int _exitCode;
    private int _disposed;

    public AutoGenFrameSink(FrameSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _frameSize = checked(options.Width * options.Height * 3);
        _input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _encodeTask = Task.Run(() => EncodeAsync(_cts.Token));
    }

    public bool IsRunning => _running && Volatile.Read(ref _disposed) == 0;

    public int? ExitCode => _finished ? _exitCode : null;

    public string LastError { get; private set; } = string.Empty;

    public async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException(string.IsNullOrEmpty(LastError)
                ? "编码器未运行"
                : $"编码器未运行：{LastError}");
        }

        if (frame.Length < _frameSize)
        {
            throw new ArgumentException("缓冲区不足一帧 RGB24", nameof(frame));
        }

        var buffer = _pool.TryTake(out var pooled) ? pooled : new byte[_frameSize];
        frame.Span[.._frameSize].CopyTo(buffer);

        try
        {
            await _input.Writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            _pool.Add(buffer);
            throw new InvalidOperationException(string.IsNullOrEmpty(LastError)
                ? "编码器已关闭"
                : $"编码器已关闭：{LastError}");
        }
    }

    /// <summary>关闭输入并等待编码器收尾；成功返回 0，出错返回 1，超时返回 null（并中断编码线程）。</summary>
    public async Task<int?> CompleteAsync(TimeSpan timeout)
    {
        _input.Writer.TryComplete();

        try
        {
            await _encodeTask.WaitAsync(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }

            return null;
        }

        return ExitCode;
    }

    public async Task<int?> WaitForExitAsync(TimeSpan timeout)
    {
        try
        {
            await _encodeTask.WaitAsync(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }

        return ExitCode;
    }

    private async Task EncodeAsync(CancellationToken cancellationToken)
    {
        var exitCode = 0;
        try
        {
            using var encoder = LibavEncoderSession.Open(_options);

            while (await _input.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_input.Reader.TryRead(out var buffer))
                {
                    encoder.Encode(buffer);
                    _pool.Add(buffer);
                }
            }

            encoder.Flush();
        }
        catch (OperationCanceledException)
        {
            exitCode = 1;
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            exitCode = 1;
        }
        finally
        {
            _exitCode = exitCode;
            _finished = true;
            _running = false;
            _input.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _input.Writer.TryComplete();

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            await _encodeTask.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _cts.Dispose();
    }
}
