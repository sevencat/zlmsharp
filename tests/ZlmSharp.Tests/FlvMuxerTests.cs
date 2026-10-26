using System.Buffers;
using System.Buffers.Binary;
using ZlmSharp.Codecs;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Flv;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Tests;

public class FlvMuxerTests
{
    private static FlvMuxer NewMuxer() => new(
        new H264Config { Sps = TestData.Sps720, Pps = TestData.Pps },
        new FlvMetadata { Width = 1280, Height = 720, FrameRate = 25 });

    private static MediaFrame Frame(byte[] annexB, long dts, FrameFlags flags = FrameFlags.None) =>
        MediaFrame.Create(annexB, TrackType.Video, CodecId.H264, dts, dts, flags);

    [Fact]
    public void Header_IsFlvWithVideoFlag()
    {
        var header = NewMuxer().BuildHeader();

        Assert.Equal(13, header.Length);
        Assert.Equal((byte)'F', header[0]);
        Assert.Equal((byte)'L', header[1]);
        Assert.Equal((byte)'V', header[2]);
        Assert.Equal(1, header[3]);
        Assert.Equal(0x01, header[4]); // 只有视频
        Assert.Equal(9, header[8]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(9)));
    }

    [Fact]
    public void AvcConfigTag_ContainsDecoderConfigRecord()
    {
        var muxer = NewMuxer();
        var tags = FlvTagParser.ParseAll(muxer.BuildAvcConfigTag());

        var tag = Assert.Single(tags);
        Assert.Equal(FlvTagType.Video, tag.Type);
        Assert.Equal(0u, tag.Timestamp);
        Assert.Equal(1, tag.VideoFrameType);
        Assert.Equal(0, tag.AvcPacketType);

        Assert.True(H264Avcc.TryParseConfigRecord(tag.Body.AsSpan(5), out var config, out var error), error);
        Assert.Equal(TestData.Sps720, config!.Sps);
        Assert.Equal(TestData.Pps, config.Pps);
    }

    [Fact]
    public void MetadataTag_ContainsStreamInfo()
    {
        var tags = FlvTagParser.ParseAll(NewMuxer().BuildMetadataTag());

        var tag = Assert.Single(tags);
        Assert.Equal(FlvTagType.Script, tag.Type);

        // onMetaData 必须是合法的 AMF0：属性名是不带 marker 的 key
        Assert.True(Amf0.TryDecode(tag.Body, out var values, out var error), error);
        Assert.Equal(2, values.Count);
        Assert.Equal("onMetaData", Assert.IsType<Amf0String>(values[0]).Value);

        var metadata = Assert.IsType<Amf0EcmaArray>(values[1]);
        Assert.Equal(1280, metadata.GetNumberProperty("width"));
        Assert.Equal(720, metadata.GetNumberProperty("height"));
        Assert.Equal(7, metadata.GetNumberProperty("videocodecid"));
        Assert.Equal(25, metadata.GetNumberProperty("framerate"));
        Assert.Equal("zlmsharp", metadata.GetStringProperty("encoder"));
    }

    [Fact]
    public void WriteVideoTag_ConvertsAnnexBToAvccAndWritesTimestamps()
    {
        var muxer = NewMuxer();
        var buffer = new ArrayBufferWriter<byte>(256);

        using (var configFrame = Frame(TestData.AnnexBConfig(), 0, FrameFlags.Config))
        {
            Assert.Equal(0, muxer.WriteVideoTag(configFrame, buffer));
        }

        using (var keyFrame = Frame(TestData.AnnexBKeyFrame(), 0, FrameFlags.Key))
        {
            Assert.True(muxer.WriteVideoTag(keyFrame, buffer) > 0);
        }

        using (var interFrame = Frame(TestData.AnnexBInterFrame(), 40))
        {
            Assert.True(muxer.WriteVideoTag(interFrame, buffer) > 0);
        }

        var tags = FlvTagParser.ParseAll(buffer.WrittenSpan);

        Assert.Equal(2, tags.Count);
        Assert.Equal(0u, tags[0].Timestamp);
        Assert.True(tags[0].IsKeyFrame);
        Assert.Equal(1, tags[0].AvcPacketType);
        Assert.Equal(40u, tags[1].Timestamp);
        Assert.False(tags[1].IsKeyFrame);

        // 第一个 tag 的 AVCC 负载应等于原始 NALU（不含起始码）
        var payload = tags[0].Body.AsSpan(5);
        var nalLength = (int)BinaryPrimitives.ReadUInt32BigEndian(payload);
        Assert.Equal(new byte[] { 0x65, 0x88, 0x84, 0x00, 0x11, 0x21 }, payload.Slice(4, nalLength).ToArray());
    }

    [Fact]
    public void TimestampOf_IsRelativeToFirstFrameAndWraps()
    {
        var muxer = NewMuxer();

        Assert.Equal(0u, muxer.TimestampOf(1000));
        Assert.Equal(40u, muxer.TimestampOf(1040));
        Assert.Equal(0u, muxer.TimestampOf(900)); // 时间戳回退按 0 处理
    }
}
