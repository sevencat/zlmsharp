using ZlmSharp.Codecs;
using ZlmSharp.Core;

namespace ZlmSharp.Tests;

/// <summary>无人观看计时与慢客户端丢帧统计。</summary>
public class MediaSourceStatsTests
{
    private static readonly StreamKey Key = StreamKey.Create("vhost", "live", "test");

    [Fact]
    public void IdleSince_StartsFromLastReaderLeave()
    {
        var source = new MediaSource(Key, "live");
        Assert.Null(source.LastReaderLeftAt);
        Assert.Equal(source.CreatedAt, source.IdleSince);

        var sink = source.CreateReader();
        source.RemoveReader(sink);

        Assert.NotNull(source.LastReaderLeftAt);
        Assert.True(source.IdleSince >= source.CreatedAt);
    }

    [Fact]
    public void TotalDroppedFrames_CountsFramesRejectedByFullQueue()
    {
        var source = new MediaSource(Key, "live", new MediaSourceOptions { ReaderQueueCapacity = 1 });
        using var sink = source.CreateReader();

        for (var index = 0; index < 5; index++)
        {
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBKeyFrame(),
                TrackType.Video,
                CodecId.H264,
                index * 40L,
                index * 40L,
                FrameFlags.Key));
        }

        Assert.Equal(4, source.TotalDroppedFrames);
        Assert.Equal(1, source.TotalPendingFrames);
    }

    [Fact]
    public void TotalDroppedFrames_IsZeroWithoutReaders()
    {
        var source = new MediaSource(Key, "live");
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBKeyFrame(),
            TrackType.Video,
            CodecId.H264,
            0,
            0,
            FrameFlags.Key));

        Assert.Equal(0, source.TotalDroppedFrames);
    }
}
