using System.Buffers.Binary;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>
/// RTCP 接收者报告（RR，PT=201，带 1 个 report block，32 字节）。
/// 推流场景中我们是接收方，需要回 RR 让发送方了解丢包/抖动，否则部分推流器会主动断开。
/// </summary>
public static class RtcpReceiverReport
{
    public const int Length = 32;

    private const byte PayloadType = 201;

    /// <summary>构造 RR。</summary>
    /// <param name="ssrc">本端 SSRC（RR 包的发送者标识）。</param>
    /// <param name="sourceSsrc">被报告的发送方 SSRC（来自其 RTP/SR）。</param>
    /// <param name="fractionLost">自上次报告以来的丢包率（0~255，即 0~100%×2.56）。</param>
    /// <param name="cumulativeLost">累计丢包数（可为负，表示重复包）。</param>
    /// <param name="highestSequence">收到过的最大 RTP 序列号（含周期回绕扩展前 16 位）。</param>
    /// <param name="jitter">到达间隔抖动（时钟单位）。</param>
    /// <param name="lastSr">上一个发送方报告的 NTP 中间 32 位。</param>
    /// <param name="delaySinceLastSr">距上一个 SR 的延迟，单位 1/65536 秒。</param>
    public static byte[] Build(
        uint ssrc,
        uint sourceSsrc,
        byte fractionLost,
        int cumulativeLost,
        uint highestSequence,
        uint jitter,
        uint lastSr,
        uint delaySinceLastSr)
    {
        var report = new byte[Length];
        report[0] = 0x81; // V=2, P=0, RC=1
        report[1] = PayloadType;
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(2), (Length / 4) - 1);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(4), ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(8), sourceSsrc);
        report[12] = fractionLost;

        // 24 位有符号累计丢包
        var lost = Math.Clamp(cumulativeLost, -0x800000, 0x7FFFFF);
        report[13] = (byte)((lost >> 16) & 0xFF);
        report[14] = (byte)((lost >> 8) & 0xFF);
        report[15] = (byte)(lost & 0xFF);

        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(16), highestSequence);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(20), jitter);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(24), lastSr);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(28), delaySinceLastSr);
        return report;
    }

    /// <summary>把时长折算成 DLSR 单位（1/65536 秒）。</summary>
    public static uint ToDelayUnits(TimeSpan elapsed) =>
        (uint)Math.Clamp(elapsed.TotalSeconds * 65536.0, 0, uint.MaxValue);
}

/// <summary>RTCP 包解析（只需要从 SR 里取 SSRC 与 NTP 中间 32 位）。</summary>
public static class RtcpPacket
{
    private const byte SenderReportType = 200;

    /// <summary>解析一条 SR；不是 SR 或长度不足返回 false。</summary>
    public static bool TryParseSenderReport(ReadOnlySpan<byte> packet, out uint ssrc, out uint ntpMiddle)
    {
        ssrc = 0;
        ntpMiddle = 0;

        if (packet.Length < 28 || packet[1] != SenderReportType || (packet[0] >> 6) != 2)
        {
            return false;
        }

        ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        var ntp = BinaryPrimitives.ReadUInt64BigEndian(packet[8..]);
        ntpMiddle = (uint)(ntp >> 16);
        return true;
    }

    /// <summary>复合包可能包含多个 RTCP 包，逐个回调。</summary>
    public static IEnumerable<(byte Type, ReadOnlyMemory<byte> Payload)> Enumerate(byte[] packet)
    {
        var offset = 0;
        while (offset + 4 <= packet.Length)
        {
            var length = (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 2)) + 1) * 4;
            if (offset + length > packet.Length || length <= 0)
            {
                break;
            }

            yield return (packet[offset + 1], new ReadOnlyMemory<byte>(packet, offset, length));
            offset += length;
        }
    }
}
