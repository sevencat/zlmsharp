using ZlmSharp.Core;

namespace ZlmSharp.Tests;

public class MediaSourceTests
{
    private static MediaSource NewSource(MediaSourceOptions? options = null) =>
        new(StreamKey.Create("vhost", "live", "test"), "live", options);

    private static MediaFrame Video(long dts, FrameFlags flags = FrameFlags.None, byte fill = 1) =>
        MediaFrame.Create(new[] { fill, fill }, TrackType.Video, CodecId.H264, dts, dts, flags);

    [Fact]
    public void AddFrame_BroadcastsToReaders()
    {
        using var source = NewSource();
        using var sink = source.CreateReader();

        source.AddFrame(Video(0, FrameFlags.Config));
        source.AddFrame(Video(40, FrameFlags.Key));

        Assert.Equal(2, sink.PendingCount);
        Assert.Equal(1, source.ReaderCount);
        Assert.True(source.Video.Ready);

        Assert.True(sink.TryRead(out var first));
        Assert.NotNull(first);
        Assert.True(first!.IsConfig);
        first.Dispose();

        Assert.True(sink.TryRead(out var second));
        second!.Dispose();
    }

    [Fact]
    public void NewReader_ReceivesGopStartingFromConfig()
    {
        using var source = NewSource();
        source.AddFrame(Video(0, FrameFlags.Config));
        source.AddFrame(Video(40, FrameFlags.Key));
        source.AddFrame(Video(80));
        source.AddFrame(Video(120));
        source.AddFrame(Video(160, FrameFlags.Key));
        source.AddFrame(Video(200));

        using var sink = source.CreateReader();

        // 应从上一次关键帧开始：key(160) + 200
        Assert.Equal(2, sink.PendingCount);
        Assert.True(sink.TryRead(out var key));
        Assert.True(key!.IsKeyFrame);
        key.Dispose();
        Assert.True(sink.TryRead(out var tail));
        tail!.Dispose();
    }

    [Fact]
    public void RingBuffer_TrimsByFrameCount()
    {
        using var source = NewSource(new MediaSourceOptions { MaxFrameCount = 3, MaxFrameMs = 0 });

        for (var index = 0; index < 10; index++)
        {
            source.AddFrame(Video(index * 40L));
        }

        Assert.Equal(3, source.FrameCacheCount);
    }

    [Fact]
    public void SlowReader_DropsFramesButKeepsStream()
    {
        using var source = NewSource(new MediaSourceOptions { ReaderQueueCapacity = 1 });
        using var sink = source.CreateReader();

        for (var index = 0; index < 5; index++)
        {
            source.AddFrame(Video(index * 40L, index == 0 ? FrameFlags.Key : FrameFlags.None));
        }

        Assert.Equal(1, sink.PendingCount);
        Assert.True(sink.DroppedFrames >= 1);
        Assert.Equal(1, source.ReaderCount);
    }

    [Fact]
    public void ReaderCount_ChangesRaiseEvents()
    {
        using var source = NewSource();
        var counts = new List<int>();
        source.ReaderCountChanged += (_, count) => counts.Add(count);

        var sink = source.CreateReader();
        Assert.True(source.RemoveReader(sink));

        Assert.Equal(new[] { 1, 0 }, counts);
        Assert.Equal(0, source.ReaderCount);
    }

    [Fact]
    public void Close_ClosesReadersAndIsIdempotent()
    {
        var source = NewSource();
        var sink = source.CreateReader();
        var closed = 0;
        source.Closed += _ => closed++;

        source.Close();
        source.Close();

        Assert.True(source.IsClosed);
        Assert.True(sink.IsClosed);
        Assert.Equal(1, closed);
        Assert.False(sink.Push(Video(0)));
    }

    [Fact]
    public void AddFrame_AfterClose_DoesNotThrow()
    {
        using var source = NewSource();
        source.Close();

        source.AddFrame(Video(0));

        Assert.Equal(0, source.FrameCacheCount);
    }
}
