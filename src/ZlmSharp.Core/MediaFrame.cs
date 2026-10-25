using System.Buffers;

namespace ZlmSharp.Core;

/// <summary>
/// 一帧媒体数据。缓冲区来自 <see cref="ArrayPool{T}"/>，通过引用计数在多个读者之间共享；
/// 最后一个 <see cref="Dispose"/> 负责归还缓冲区。所有读者用完必须 Dispose。
/// </summary>
public sealed class MediaFrame : IDisposable
{
    private byte[]? _buffer;
    private int _refCount = 1;
    private readonly ReadOnlyMemory<byte> _data;

    private MediaFrame(
        byte[] buffer,
        int length,
        TrackType trackType,
        CodecId codecId,
        long dts,
        long pts,
        FrameFlags flags)
    {
        _buffer = buffer;
        _data = new ReadOnlyMemory<byte>(buffer, 0, length);
        TrackType = trackType;
        CodecId = codecId;
        Dts = dts;
        Pts = pts;
        Flags = flags;
    }

    public TrackType TrackType { get; }

    public CodecId CodecId { get; }

    /// <summary>解码时间戳（毫秒，流内单调递增，从 0 起）。</summary>
    public long Dts { get; }

    /// <summary>显示时间戳（毫秒）。一期无 B 帧时与 <see cref="Dts"/> 相同。</summary>
    public long Pts { get; }

    public FrameFlags Flags { get; }

    public int Length => _data.Length;

    public ReadOnlyMemory<byte> Data => _data;

    public bool IsConfig => (Flags & FrameFlags.Config) != 0;

    public bool IsKeyFrame => (Flags & FrameFlags.Key) != 0;

    public int RefCount => Volatile.Read(ref _refCount);

    public static MediaFrame Create(
        ReadOnlySpan<byte> data,
        TrackType trackType,
        CodecId codecId,
        long dts,
        long pts,
        FrameFlags flags = FrameFlags.None)
    {
        if (data.Length == 0)
        {
            throw new ArgumentException("帧数据不能为空", nameof(data));
        }

        var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buffer);
        return new MediaFrame(buffer, data.Length, trackType, codecId, dts, pts, flags);
    }

    /// <summary>增加引用；帧已释放时返回 false（说明读者拿到的是过期帧）。</summary>
    public bool TryAddRef()
    {
        while (true)
        {
            var current = Volatile.Read(ref _refCount);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public void Dispose()
    {
        var remaining = Interlocked.Decrement(ref _refCount);
        if (remaining != 0)
        {
            return;
        }

        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override string ToString() =>
        $"{TrackType}/{CodecId} dts={Dts} pts={Pts} len={Length} flags={Flags}";
}
