using System.Buffers.Binary;
using ZlmSharp.Codecs;
using ZlmSharp.Protocols.Rtsp;

namespace ZlmSharp.Tests;

public class RtpPacketizerTests
{
    [Fact]
    public void Packetize_SingleNalUnit_WritesOneRtpPacket()
    {
        var packetizer = new RtpPacketizer(0x11223344, payloadType: 96, mtu: 1400);
        var frame = TestData.AnnexBKeyFrame();

        var packets = packetizer.Packetize(frame, 0);

        var packet = Assert.Single(packets);
        Assert.Equal(0x80, packet[0]);              // V=2
        Assert.Equal(0xE0, packet[1]);              // M=1, PT=96
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8)));
        Assert.Equal(RtpPacketizer.HeaderSize + 6, packet.Length);
        Assert.Equal(0x65, packet[12]);
        Assert.Equal(1, packetizer.SequenceNumber);
    }

    [Fact]
    public void Packetize_LargeNalUnit_SplitsIntoFuA()
    {
        var packetizer = new RtpPacketizer(1, payloadType: 96, mtu: 1400);
        var nalUnit = new byte[3000];
        nalUnit[0] = 0x65;
        for (var i = 1; i < nalUnit.Length; i++)
        {
            nalUnit[i] = (byte)(i & 0xFF);
        }

        var frame = new byte[4 + nalUnit.Length];
        frame[3] = 1;
        nalUnit.CopyTo(frame, 4);

        var packets = packetizer.Packetize(frame, 3600);

        Assert.Equal(3, packets.Count);
        Assert.All(packets, packet => Assert.Equal(3600u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4))));

        // 首包：S=1、E=0、marker=0
        Assert.Equal(0x80, packets[0][13] & 0x80);
        Assert.Equal(0x00, packets[0][13] & 0x40);
        Assert.Equal(0x00, packets[0][1] & 0x80);
        Assert.Equal(0x7C, packets[0][12]); // FU indicator: NRI 0x60 | 28

        // 中间包：S=0、E=0
        Assert.Equal(0x00, packets[1][13] & 0xC0);

        // 末包：E=1、marker=1
        Assert.Equal(0x40, packets[^1][13] & 0x40);
        Assert.Equal(0x80, packets[^1][1] & 0x80);

        for (var i = 0; i < packets.Count; i++)
        {
            Assert.Equal(i, BinaryPrimitives.ReadUInt16BigEndian(packets[i].AsSpan(2)));
            Assert.True(packets[i].Length <= 1400);
        }
    }

    [Fact]
    public void Packetize_TimestampAndSequenceCarryAcrossFrames()
    {
        var packetizer = new RtpPacketizer(7, sequenceNumber: 10);

        var first = packetizer.Packetize(TestData.AnnexBKeyFrame(), 0);
        var second = packetizer.Packetize(TestData.AnnexBInterFrame(), 3600);

        Assert.Equal(10, BinaryPrimitives.ReadUInt16BigEndian(first[0].AsSpan(2)));
        Assert.Equal(11, BinaryPrimitives.ReadUInt16BigEndian(second[0].AsSpan(2)));
        Assert.Equal(3600u, BinaryPrimitives.ReadUInt32BigEndian(second[0].AsSpan(4)));
    }

    [Fact]
    public void Packetize_MultipleNalUnits_MarksOnlyLastPacket()
    {
        var packetizer = new RtpPacketizer(2);
        var frame = new List<byte>();
        frame.AddRange([0, 0, 0, 1]);
        frame.AddRange([0x67, 0x42, 0x00, 0x1F]);
        frame.AddRange([0, 0, 0, 1]);
        frame.AddRange([0x65, 0x88, 0x84]);

        var packets = packetizer.Packetize(frame.ToArray(), 0);

        Assert.Equal(2, packets.Count);
        Assert.Equal(0x00, packets[0][1] & 0x80);
        Assert.Equal(0x80, packets[1][1] & 0x80);
        Assert.Equal(0x67, packets[0][12]); // NAL 头原样保留
        Assert.Equal(0x65, packets[1][12]);
    }
}
