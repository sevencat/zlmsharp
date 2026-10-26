using System.Buffers.Binary;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>RTCP 发送者报告（SR，PT=200，不带 report block，28 字节）。</summary>
/// <remarks>
/// 客户端（尤其 VLC）依赖 SR 里的 NTP / RTP 时间戳对来建立同步源，
/// 未发 SR 时表现为黑屏或迟迟不出画面。TCP interleaved 与 UDP 都要发。
/// </remarks>
public static class RtcpSenderReport
{
    /// <summary>SR 包长（无 report block）。</summary>
    public const int Length = 28;

    private const byte PayloadType = 200;
    private const long NtpEpochOffset = 2_208_988_800L; // 1900-01-01 与 1970-01-01 的秒差
    private const uint RtpClockRate = 90_000;

    /// <summary>构造 SR。</summary>
    /// <param name="ssrc">同步源标识，与 RTP 头一致。</param>
    /// <param name="ntp">NTP 64 位时间戳（高 32 位秒、低 32 位小数）。</param>
    /// <param name="rtpTimestamp">与该 NTP 时刻对应的 RTP 时间戳（90kHz）。</param>
    /// <param name="packetCount">已发送 RTP 包数。</param>
    /// <param name="octetCount">已发送负载字节数（不含 RTP 头）。</param>
    public static byte[] Build(
        uint ssrc,
        ulong ntp,
        uint rtpTimestamp,
        uint packetCount,
        uint octetCount)
    {
        var report = new byte[Length];
        report[0] = 0x80; // V=2, P=0, RC=0
        report[1] = PayloadType;
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(2), (Length / 4) - 1);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(4), ssrc);
        BinaryPrimitives.WriteUInt64BigEndian(report.AsSpan(8), ntp);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(16), rtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(20), packetCount);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(24), octetCount);
        return report;
    }

    /// <summary>UTC 时间 → NTP 64 位时间戳（高 32 位秒、低 32 位小数）。</summary>
    public static ulong ToNtp(DateTimeOffset time)
    {
        var seconds = (ulong)(time.ToUnixTimeSeconds() + NtpEpochOffset);
        var remainder = (ulong)(time.UtcTicks % TimeSpan.TicksPerSecond);
        var fraction = remainder * (1UL << 32) / (ulong)TimeSpan.TicksPerSecond;
        return (seconds << 32) | fraction;
    }

    /// <summary>经过的时长折算成 90kHz 的 RTP 时钟增量。</summary>
    public static uint ToRtpDelta(TimeSpan elapsed) => (uint)Math.Max(0, elapsed.TotalSeconds * RtpClockRate);
}
