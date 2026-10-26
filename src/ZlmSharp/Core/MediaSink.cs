using System.Threading.Channels;

namespace ZlmSharp.Core;

/// <summary>
/// 一个观看者（协议会话）的帧接收端。有界队列 + 满则丢帧，避免慢客户端拖垮整条流。
/// <see cref="Push"/> 成功时内部已持有一次引用，读者消费后必须 Dispose 帧。
/// </summary>
public sealed class MediaSink : IDisposable
{
    private readonly Channel<MediaFrame> _channel;
    private int _closed;
    private long _droppedFrames;

    public MediaSink(MediaSource source, int capacity)
    {
        Source = source;
        _channel = Channel.CreateBounded<MediaFrame>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public MediaSource Source { get; }

    public long Id { get; internal set; }

    public int PendingCount => _channel.Reader.Count;

    /// <summary>因队列满/已关闭而丢弃的帧数。</summary>
    public long DroppedFrames => Volatile.Read(ref _droppedFrames);

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public bool Push(MediaFrame frame)
    {
        if (IsClosed || !frame.TryAddRef())
        {
            return false;
        }

        if (_channel.Writer.TryWrite(frame))
        {
            return true;
        }

        frame.Dispose();
        Interlocked.Increment(ref _droppedFrames);
        return false;
    }

    public IAsyncEnumerable<MediaFrame> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public bool TryRead(out MediaFrame? frame) => _channel.Reader.TryRead(out frame);

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _channel.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        Close();
        while (_channel.Reader.TryRead(out var frame))
        {
            frame.Dispose();
        }
    }

    public override string ToString() => $"sink#{Id} of {Source.Key} pending={PendingCount}";
}
