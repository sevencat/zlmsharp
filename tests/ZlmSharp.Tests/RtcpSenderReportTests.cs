using System.Buffers.Binary;
using ZlmSharp.Protocols.Rtsp;

namespace ZlmSharp.Tests;

public class RtcpSenderReportTests
{
    [Fact]
    public void ToNtp_Uses1900Epoch()
    {
        var ntp = RtcpSenderReport.ToNtp(DateTimeOffset.FromUnixTimeSeconds(0));
        Assert.Equal((ulong)2_208_988_800 << 32, ntp);

        var withFraction = RtcpSenderReport.ToNtp(DateTimeOffset.FromUnixTimeMilliseconds(1500));
        Assert.Equal(((ulong)2_208_988_801 << 32) | 0x8000_0000UL, withFraction);
    }

    [Fact]
    public void ToRtpDelta_ConvertsSecondsTo90kHz()
    {
        Assert.Equal(90_000u, RtcpSenderReport.ToRtpDelta(TimeSpan.FromSeconds(1)));
        Assert.Equal(0u, RtcpSenderReport.ToRtpDelta(TimeSpan.FromMilliseconds(-5)));
    }

    [Fact]
    public void Build_WritesSenderReportFields()
    {
        var report = RtcpSenderReport.Build(
            0x01020304u,
            0x1122334455667788UL,
            0x00000186A0u,
            7,
            1400);

        Assert.Equal(28, report.Length);
        Assert.Equal(28, RtcpSenderReport.Length);
        Assert.Equal(0x80, report[0]);
        Assert.Equal(200, report[1]);
        Assert.Equal(6, BinaryPrimitives.ReadUInt16BigEndian(report.AsSpan(2)));
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(4)));
        Assert.Equal(0x1122334455667788UL, BinaryPrimitives.ReadUInt64BigEndian(report.AsSpan(8)));
        Assert.Equal(0x00000186A0u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(16)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(20)));
        Assert.Equal(1400u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(24)));
    }
}
