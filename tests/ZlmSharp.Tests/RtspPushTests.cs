using System.Buffers.Binary;
using ZlmSharp.Protocols.Rtsp;

namespace ZlmSharp.Tests;

/// <summary>RTSP 推流：SDP 解析、RTP 解包（单包/STAP-A/FU-A）、目标 URI 解析。</summary>
public class RtspPushTests
{
    private const string Sdp = "v=0\r\n" +
                               "o=- 0 0 IN IP4 127.0.0.1\r\n" +
                               "s=No Name\r\n" +
                               "c=IN IP4 127.0.0.1\r\n" +
                               "t=0 0\r\n" +
                               "a=tool:libavformat 61.7.100\r\n" +
                               "m=video 0 RTP/AVP 96\r\n" +
                               "b=AS:2000\r\n" +
                               "a=rtpmap:96 H264/90000\r\n" +
                               "a=fmtp:96 packetization-mode=1; sprop-parameter-sets=Z2QAH6zZQFAFuhAAAAMAEAAAAwPI8YMZYA==,aO48gA==; profile-level-id=64001F\r\n" +
                               "a=control:streamid=0\r\n";

    [Fact]
    public void SdpParser_ReadsH264TrackAndParameterSets()
    {
        Assert.True(SdpParser.TryParseH264(Sdp, out var track, out var error), error);

        Assert.NotNull(track);
        Assert.Equal(96, track!.PayloadType);
        Assert.Equal(90000, track.ClockRate);
        Assert.Equal("streamid=0", track.Control);
        Assert.True(track.HasParameterSets);
        Assert.Equal(7, track.Sps![0] & 0x1F);
        Assert.Equal(8, track.Pps![0] & 0x1F);
    }

    [Fact]
    public void SdpParser_WithoutSprop_StillAcceptsTrack()
    {
        var sdp = Sdp.Replace("a=fmtp:96 packetization-mode=1; sprop-parameter-sets=Z2QAH6zZQFAFuhAAAAMAEAAAAwPI8YMZYA==,aO48gA==; profile-level-id=64001F\r\n", string.Empty);

        Assert.True(SdpParser.TryParseH264(sdp, out var track, out var error), error);
        Assert.False(track!.HasParameterSets);
        Assert.Null(track.Sps);
    }

    [Fact]
    public void SdpParser_RejectsNonH264()
    {
        var sdp = Sdp.Replace("H264/90000", "H265/90000");

        Assert.False(SdpParser.TryParseH264(sdp, out _, out var error));
        Assert.Contains("H.264", error);
    }

    [Fact]
    public void SdpParser_RejectsInvalidSpropBase64()
    {
        var sdp = Sdp.Replace("Z2QAH6zZQFAFuhAAAAMAEAAAAwPI8YMZYA==", "!!!not-base64!!!");

        Assert.False(SdpParser.TryParseH264(sdp, out _, out var error));
        Assert.Contains("base64", error);
    }

    [Fact]
    public void RtpHeader_ParsesFields()
    {
        var payload = new byte[] { 0x65, 0x11, 0x22 };
        var packet = BuildRtp(payload, sequence: 7, timestamp: 90_000, marker: true);

        Assert.True(RtpPacketHeader.TryParse(packet, out var header));
        Assert.Equal(96, header.PayloadType);
        Assert.True(header.Marker);
        Assert.Equal(7, header.SequenceNumber);
        Assert.Equal(90_000u, header.Timestamp);
        Assert.Equal(12, header.PayloadOffset);
        Assert.Equal(payload.Length, header.PayloadLength);
        Assert.Equal(payload, packet.AsSpan(header.PayloadOffset, header.PayloadLength).ToArray());
    }

    [Fact]
    public void Depacketizer_SingleNal_EmitsAnnexBOnMarker()
    {
        var depacketizer = new H264RtpDepacketizer();
        var nal = new byte[] { 0x65, 0x88, 0x84 };

        Assert.True(depacketizer.Push(nal, marker: true, out var accessUnit, out var keyFrame));

        Assert.True(keyFrame);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65, 0x88, 0x84 }, accessUnit);
    }

    [Fact]
    public void Depacketizer_FuA_ReassemblesNal()
    {
        var depacketizer = new H264RtpDepacketizer();
        var data = new byte[] { 0x88, 0x84, 0x00, 0x11, 0x21 };

        Assert.False(depacketizer.Push([0x7C, 0x85, data[0], data[1]], marker: false, out _, out _));
        Assert.False(depacketizer.Push([0x7C, 0x05, data[2]], marker: false, out _, out _));
        Assert.True(depacketizer.Push([0x7C, 0x45, data[3], data[4]], marker: true, out var accessUnit, out var keyFrame));

        Assert.True(keyFrame);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65 }.Concat(data).ToArray(), accessUnit);
    }

    [Fact]
    public void Depacketizer_FuA_MissingStart_IsDropped()
    {
        var depacketizer = new H264RtpDepacketizer();

        Assert.False(depacketizer.Push([0x7C, 0x05, 0x11], marker: true, out _, out _));
        Assert.False(depacketizer.Push([0x7C, 0x05, 0x22], marker: true, out _, out _));
    }

    [Fact]
    public void Depacketizer_StapA_EmitsMultipleNals()
    {
        var depacketizer = new H264RtpDepacketizer();
        var sps = new byte[] { 0x67, 0x42, 0x00, 0x1F };
        var pps = new byte[] { 0x68, 0xCE, 0x3C, 0x80 };

        var payload = new byte[1 + 2 + sps.Length + 2 + pps.Length];
        payload[0] = 0x78;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), (ushort)sps.Length);
        sps.CopyTo(payload, 3);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3 + sps.Length), (ushort)pps.Length);
        pps.CopyTo(payload, 5 + sps.Length);

        Assert.True(depacketizer.Push(payload, marker: true, out var accessUnit, out var keyFrame));

        Assert.False(keyFrame);
        var expected = new byte[] { 0, 0, 0, 1 }
            .Concat(sps)
            .Concat(new byte[] { 0, 0, 0, 1 })
            .Concat(pps)
            .ToArray();
        Assert.Equal(expected, accessUnit);
    }

    [Fact]
    public void RtspUri_ParsesTrackAndVhost()
    {
        Assert.True(RtspUri.TryParse("rtsp://127.0.0.1:554/live/cam/trackID=0", out var app, out var stream, out var vhost));
        Assert.Equal("live", app);
        Assert.Equal("cam", stream);
        Assert.Null(vhost);
        Assert.Equal("rtsp://127.0.0.1:554/live/cam", RtspUri.BuildBaseUri("rtsp://127.0.0.1:554/live/cam/trackID=0"));

        Assert.True(RtspUri.TryParse("rtsp://127.0.0.1/live/cam/streamid=0", out _, out stream, out _));
        Assert.Equal("cam", stream);

        Assert.True(RtspUri.TryParse("rtsp://127.0.0.1/vhost/live/cam", out app, out stream, out vhost));
        Assert.Equal("vhost", vhost);
        Assert.Equal("live", app);
        Assert.Equal("cam", stream);

        // 普通流名不能被误判成轨道段
        Assert.Equal("rtsp://127.0.0.1/live/track1", RtspUri.BuildBaseUri("rtsp://127.0.0.1/live/track1"));
        Assert.False(RtspUri.TryParse("rtsp://127.0.0.1/live", out _, out _, out _));
    }

    private static byte[] BuildRtp(byte[] payload, ushort sequence, uint timestamp, bool marker)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = (byte)((marker ? 0x80 : 0) | 96);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0x12345678);
        payload.CopyTo(packet, 12);
        return packet;
    }
}
