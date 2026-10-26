using ZlmSharp.Codecs;
using ZlmSharp.Protocols.Rtsp;

namespace ZlmSharp.Tests;

public class SdpBuilderTests
{
    [Fact]
    public void BuildH264_ContainsTrackAndParameterSets()
    {
        var config = new H264Config { Sps = TestData.Sps720, Pps = TestData.Pps };

        var sdp = SdpBuilder.BuildH264(config);

        Assert.StartsWith("v=0\r\n", sdp);
        Assert.Contains("m=video 0 RTP/AVP 96\r\n", sdp);
        Assert.Contains("a=rtpmap:96 H264/90000\r\n", sdp);
        Assert.Contains("packetization-mode=1", sdp);
        Assert.Contains("profile-level-id=f4001f", sdp); // SPS[1..3] = F4 00 1F
        Assert.Contains(
            $"sprop-parameter-sets={Convert.ToBase64String(TestData.Sps720)},{Convert.ToBase64String(TestData.Pps)}",
            sdp);
        Assert.Contains("a=control:trackID=0\r\n", sdp);
    }

    [Fact]
    public void BuildProfileLevelId_UsesSpsBytes()
    {
        var config = new H264Config { Sps = TestData.Sps720, Pps = TestData.Pps };

        Assert.Equal("f4001f", SdpBuilder.BuildProfileLevelId(config));
    }

    [Fact]
    public void BuildProfileLevelId_ShortSps_FallsBackToBaseline()
    {
        var config = new H264Config { Sps = [0x67], Pps = TestData.Pps };

        Assert.Equal("42001e", SdpBuilder.BuildProfileLevelId(config));
    }
}
