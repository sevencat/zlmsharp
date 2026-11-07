using System.Collections.Concurrent;
using System.Threading.Channels;
using ZlmSharp.Extra.Media;

namespace ZlmSharp.Media.AutoGen;

/// <summary>
/// 进程内顺序解码：后台解码线程 → 有界队列 → 读者逐帧取走（不丢帧、带背压）。
/// 与 exe 实现的 <c>ProcessFrameReader</c> 语义一致。
/// </summary>
internal sealed class AutoGenFrameReader : IFrameReader
{
    private readonly string _url;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _frames;
    private readonly ConcurrentBag<byte[]> _pool = new();
    private readonly int _frameSize;
    private readonly Task _pumpTask;
    private volatile bool _running = true;
    private int _disposed;
    private int? _exitCode;

    public AutoGenFrameReader(string url, int width, int height)
    {
        _url = url;
        Width = width;
        Height = height;
        _frameSize = checked(width * height * 3);
        _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    public int Width { get; }

    public int Height { get; }

    public bool IsRunning => _running && Volatile.Read(ref _disposed) == 0;

    public int? ExitCode => _running ? null : _exitCode;

    public string LastError { get; private set; } = string.Empty;

    public async Task<bool> ReadIntoAsync(Memory<byte> frame, CancellationToken cancellationToken)
    {
        if (frame.Length < _frameSize)
        {
            throw new ArgumentException("缓冲区不足一帧 RGB24", nameof(frame));
        }

        if (!await _frames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (!_frames.Reader.TryRead(out var buffer))
        {
            return false;
        }

        buffer.AsSpan(0, _frameSize).CopyTo(frame.Span);
        _pool.Add(buffer);
        return true;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var exitCode = 0;
        try
        {
            using var session = LibavDecoderSession.Open(_url, Width, Height);

            while (!cancellationToken.IsCancellationRequested)
            {
                var buffer = _pool.TryTake(out var pooled) ? pooled : new byte[_frameSize];
                if (!session.TryReadFrame(buffer, cancellationToken))
                {
                    return;
                }

                await _frames.Writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
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
            _running = false;
            _frames.Writer.TryComplete();
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
            await _pumpTask.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _frames.Writer.TryComplete();
        _cts.Dispose();
    }
}
