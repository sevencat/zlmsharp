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
    private int _waitKeyFrame;
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

    /// <summary>因队列满/已关闭/追赶丢帧而丢弃的帧数。</summary>
    public long DroppedFrames => Volatile.Read(ref _droppedFrames);

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>是否允许积压超限时丢弃积压追赶实时（仅直播播放端）。</summary>
    public bool AllowCatchUp { get; internal set; }

    /// <summary>是否正在等下一个关键帧（此期间非关键帧一律丢弃，避免花屏）。</summary>
    public bool IsWaitingKeyFrame => Volatile.Read(ref _waitKeyFrame) != 0;

    public bool Push(MediaFrame frame)
    {
        if (IsClosed || !frame.TryAddRef())
        {
            return false;
        }

        if (Volatile.Read(ref _waitKeyFrame) != 0)
        {
            if (frame.TrackType == TrackType.Video && frame.IsKeyFrame)
            {
                Volatile.Write(ref _waitKeyFrame, 0);
            }
            else
            {
                frame.Dispose();
                Interlocked.Increment(ref _droppedFrames);
                return false;
            }
        }

        if (_channel.Writer.TryWrite(frame))
        {
            return true;
        }

        frame.Dispose();
        Interlocked.Increment(ref _droppedFrames);
        return false;
    }

    /// <summary>丢弃队列里尚未发送的帧（用于追赶实时），随后配合 <see cref="WaitKeyFrame"/>。</summary>
    public void DropBacklog()
    {
        var dropped = 0;
        while (_channel.Reader.TryRead(out var frame))
        {
            frame?.Dispose();
            dropped++;
        }

        if (dropped > 0)
        {
            Interlocked.Add(ref _droppedFrames, dropped);
        }
    }

    /// <summary>从现在起丢弃非关键帧，直到下一个关键帧到达。</summary>
    public void WaitKeyFrame() => Volatile.Write(ref _waitKeyFrame, 1);

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
