using System.Buffers.Binary;
using ZlmSharp.Codecs;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>
/// H.264 → RTP 打包（RFC 6184）：小于 MTU 的 NALU 单包发送，超过则 FU-A 分片。
/// 输入为 Annex-B 帧；输出是带 12 字节 RTP 头的完整包。
/// </summary>
public sealed class RtpPacketizer
{
    public const int HeaderSize = 12;

    private const byte FuAType = 28;

    private readonly int _payloadType;
    private readonly int _mtu;
    private readonly uint _ssrc;

    private ushort _sequenceNumber;

    public RtpPacketizer(uint ssrc, int payloadType = 96, int mtu = 1400, ushort sequenceNumber = 0)
    {
        if (mtu <= HeaderSize + 2)
        {
            throw new ArgumentOutOfRangeException(nameof(mtu));
        }

        _ssrc = ssrc;
        _payloadType = payloadType;
        _mtu = mtu;
        _sequenceNumber = sequenceNumber;
    }

    public uint Ssrc => _ssrc;

    public ushort SequenceNumber => _sequenceNumber;

    /// <summary>把一个 Annex-B 访问单元打包成若干 RTP 包，最后一个包置 marker 位。</summary>
    public List<byte[]> Packetize(ReadOnlySpan<byte> annexB, uint timestamp)
    {
        var packets = new List<byte[]>();
        var remaining = annexB;

        while (!remaining.IsEmpty)
        {
            var consumed = AnnexB.ReadNext(remaining, out var nalUnit);
            if (consumed <= 0)
            {
                break;
            }

            if (!nalUnit.IsEmpty)
            {
                PacketizeNalUnit(nalUnit, timestamp, packets);
            }

            if (consumed >= remaining.Length)
            {
                break;
            }

            remaining = remaining[consumed..];
        }

        if (packets.Count > 0)
        {
            packets[^1][1] |= 0x80; // M=1
        }

        return packets;
    }

    private void PacketizeNalUnit(ReadOnlySpan<byte> nalUnit, uint timestamp, List<byte[]> packets)
    {
        var maxPayload = _mtu - HeaderSize;

        if (nalUnit.Length <= maxPayload)
        {
            packets.Add(BuildPacket(nalUnit, timestamp));
            return;
        }

        var indicator = (byte)((nalUnit[0] & 0xE0) | FuAType);
        var nalHeader = (byte)(nalUnit[0] & 0x1F);
        var chunkSize = maxPayload - 2;
        var offset = 1;

        while (offset < nalUnit.Length)
        {
            var size = Math.Min(chunkSize, nalUnit.Length - offset);
            var payload = new byte[2 + size];

            payload[0] = indicator;
            payload[1] = (byte)(nalHeader
                | (offset == 1 ? 0x80 : 0x00)                     // S
                | (offset + size >= nalUnit.Length ? 0x40 : 0x00)); // E

            nalUnit.Slice(offset, size).CopyTo(payload.AsSpan(2));
            packets.Add(BuildPacket(payload, timestamp));
            offset += size;
        }
    }

    private byte[] BuildPacket(ReadOnlySpan<byte> payload, uint timestamp)
    {
        var packet = new byte[HeaderSize + payload.Length];
        packet[0] = 0x80; // V=2, P=0, X=0, CC=0
        packet[1] = (byte)_payloadType;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), _sequenceNumber++);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), _ssrc);
        payload.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }
}
