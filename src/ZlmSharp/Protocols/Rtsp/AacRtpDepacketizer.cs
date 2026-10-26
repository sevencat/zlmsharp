using System.Buffers.Binary;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>
/// AAC RTP 解包（RFC 3640 <c>MPEG4-GENERIC</c>，AAC-hbr 模式）：
/// 负载 = 2 字节 AU-headers-length（bit）+ AU headers（每 AU 16 bit：size 13 / index 3）+ AU 数据。
/// 一个包里有多个 AU 时只取第一个（常见推流器每包一个 AU），避免把多帧拼成一个 sample。
/// </summary>
public sealed class AacRtpDepacketizer
{
    /// <summary>因一个 RTP 包携带多 AU 而丢弃的帧数。</summary>
    public long SkippedAuCount { get; private set; }

    public bool Push(ReadOnlySpan<byte> payload, out byte[]? accessUnit)
    {
        accessUnit = null;

        if (payload.Length < 4)
        {
            return false;
        }

        var headerBits = BinaryPrimitives.ReadUInt16BigEndian(payload);
        if (headerBits == 0 || headerBits % 8 != 0)
        {
            // 没有 AU header：整个负载按单帧处理（部分实现如此）
            accessUnit = payload.ToArray();
            return accessUnit.Length > 0;
        }

        var headerBytes = headerBits / 8;
        var dataOffset = 2 + headerBytes;
        if (headerBytes < 2 || dataOffset > payload.Length)
        {
            return false;
        }

        var auCount = headerBytes / 2;
        var size = (BinaryPrimitives.ReadUInt16BigEndian(payload[2..]) & 0x1FFF);

        if (auCount > 1)
        {
            SkippedAuCount += auCount - 1;
        }

        if (size == 0 || dataOffset + size > payload.Length)
        {
            // 长度字段不可信时回退到「剩余数据即一帧」
            size = payload.Length - dataOffset;
            if (size <= 0)
            {
                return false;
            }
        }

        accessUnit = payload.Slice(dataOffset, size).ToArray();
        return true;
    }
}
