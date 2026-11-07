using ZlmSharp.Ai;
using ZlmSharp.Extra.Media;

namespace ZlmSharp.Media.AutoGen;

/// <summary>
/// 进程内解码采样器：后台线程持续解码，只保留最新一帧（双缓冲覆盖），读不到旧帧。
/// 与 exe 实现的 <c>ProcessFrameSampler</c> 语义一致，但不经过管道。
/// </summary>
internal sealed class AutoGenFrameSampler : IFrameSampler
{
    private readonly string _url;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _frameReady = new(0, 1);
    private readonly object _gate = new();
    private readonly int _frameSize;
    private readonly Task _pumpTask;
    private byte[] _latest;
    private byte[] _pump;
    private volatile bool _running = true;
    private int _disposed;

    public AutoGenFrameSampler(string url, int width, int height)
    {
        _url = url;
        Width = width;
        Height = height;
        _frameSize = checked(width * height * 3);
        _latest = new byte[_frameSize];
        _pump = new byte[_frameSize];

        // 打开输入（RTSP 握手可能耗时）放到后台，避免阻塞调用方
        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    public int Width { get; }

    public int Height { get; }

    public bool IsRunning => _running && Volatile.Read(ref _disposed) == 0;

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

        var pixels = new byte[_frameSize];
        lock (_gate)
        {
            Buffer.BlockCopy(_latest, 0, pixels, 0, _frameSize);
        }

        return new RgbImage(pixels, Width, Height);
    }

    private void PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var session = LibavDecoderSession.Open(_url, Width, Height);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!session.TryReadFrame(_pump, cancellationToken))
                {
                    break;
                }

                lock (_gate)
                {
                    (_latest, _pump) = (_pump, _latest);
                }

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
        catch (Exception exception)
        {
            LastError = exception.Message;
        }
        finally
        {
            _running = false;
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
            // av_read_frame 带 5s 读超时，最长等它返回
            await _pumpTask.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _frameReady.Dispose();
        _cts.Dispose();
    }
}
