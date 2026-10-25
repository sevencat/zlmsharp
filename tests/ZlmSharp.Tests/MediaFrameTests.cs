using ZlmSharp.Core;

namespace ZlmSharp.Tests;

public class MediaFrameTests
{
    [Fact]
    public void Create_CopiesDataAndKeepsMetadata()
    {
        var payload = new byte[] { 1, 2, 3, 4 };

        using var frame = MediaFrame.Create(payload, TrackType.Video, CodecId.H264, 10, 20, FrameFlags.Key);

        Assert.Equal(4, frame.Length);
        Assert.Equal(payload, frame.Data.ToArray());
        Assert.Equal(10, frame.Dts);
        Assert.Equal(20, frame.Pts);
        Assert.True(frame.IsKeyFrame);
        Assert.Equal(CodecId.H264, frame.CodecId);
    }

    [Fact]
    public void Create_RejectsEmptyPayload()
    {
        Assert.Throws<ArgumentException>(() =>
            MediaFrame.Create(ReadOnlySpan<byte>.Empty, TrackType.Video, CodecId.H264, 0, 0));
    }

    [Fact]
    public void TryAddRef_IncreasesAndBlocksAfterDispose()
    {
        var frame = MediaFrame.Create(new byte[] { 1 }, TrackType.Video, CodecId.H264, 0, 0);

        Assert.Equal(1, frame.RefCount);
        Assert.True(frame.TryAddRef());
        Assert.Equal(2, frame.RefCount);

        frame.Dispose();
        Assert.Equal(1, frame.RefCount);

        frame.Dispose();
        Assert.Equal(0, frame.RefCount);
        Assert.False(frame.TryAddRef());
    }
}
