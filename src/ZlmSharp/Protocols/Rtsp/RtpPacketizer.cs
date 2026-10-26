using System.Buffers.Binary;
using ZlmSharp.Codecs;
using ZlmSharp.Core;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>
/// H.264 / H.265 → RTP 打包：H.264 按 RFC 6184（单 NALU / FU-A），
/// H.265 按 RFC 7798（单 NALU / FU，含 2 字节 payload header）。
/// 输入为 Annex-B 帧；输出是带 12 字节 RTP 头的完整包。
/// </summary>
public sealed class RtpPacketizer
{
    public const int HeaderSize = 12;

    private const byte FuAType = 28;
    private const byte H265ApType = 48;
    private const byte H265FuType = 49;

    private readonly int _payloadType;
    private readonly int _mtu;
    private readonly uint _ssrc;
    private readonly CodecId _codec;

    private ushort _sequenceNumber;

    public RtpPacketizer(
        uint ssrc,
        int payloadType = 96,
        int mtu = 1400,
        ushort sequenceNumber = 0,
        CodecId codec = CodecId.H264)
    {
        if (mtu <= HeaderSize + 3)
        {
            throw new ArgumentOutOfRangeException(nameof(mtu));
        }

        if (codec is not (CodecId.H264 or CodecId.H265))
        {
            throw new ArgumentException($"不支持的 RTP 视频编码: {codec}", nameof(codec));
        }

        _ssrc = ssrc;
        _payloadType = payloadType;
        _mtu = mtu;
        _sequenceNumber = sequenceNumber;
        _codec = codec;
    }

    public CodecId Codec => _codec;

    public uint Ssrc => _ssrc;

    public ushort SequenceNumber => _sequenceNumber;

    /// <summary>
    /// 把一个 Annex-B 访问单元打包成若干 RTP 包，最后一个包置 marker 位。
    /// <paramref name="codec"/> 为空时用构造时指定的编码（播放端在解析出源编码后可覆盖）。
    /// </summary>
    public List<byte[]> Packetize(ReadOnlySpan<byte> annexB, uint timestamp, CodecId? codec = null)
    {
        var effectiveCodec = codec ?? _codec;
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
                PacketizeNalUnit(nalUnit, timestamp, packets, effectiveCodec);
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

    private void PacketizeNalUnit(ReadOnlySpan<byte> nalUnit, uint timestamp, List<byte[]> packets, CodecId codec)
    {
        if (codec == CodecId.H265)
        {
            PacketizeH265NalUnit(nalUnit, timestamp, packets);
            return;
        }

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

    /// <summary>RFC 7798：单 NAL 包或 FU（fragmentation unit）分片。</summary>
    private void PacketizeH265NalUnit(ReadOnlySpan<byte> nalUnit, uint timestamp, List<byte[]> packets)
    {
        if (nalUnit.Length < H265NalUnit.HeaderSize)
        {
            return;
        }

        var maxPayload = _mtu - HeaderSize;

        // 单 NAL 单元包：RTP 负载就是 NAL（其 2 字节头即 RFC 7798 的 payload header）
        if (nalUnit.Length <= maxPayload)
        {
            packets.Add(BuildPacket(nalUnit, timestamp));
            return;
        }

        var chunkSize = maxPayload - 3; // 2 字节 payload header + 1 字节 FU header
        var payloadHeader0 = (byte)((nalUnit[0] & 0x81) | (H265FuType << 1));
        var payloadHeader1 = nalUnit[1];
        var nalType = (byte)((nalUnit[0] >> 1) & 0x3F);
        var offset = H265NalUnit.HeaderSize;

        while (offset < nalUnit.Length)
        {
            var size = Math.Min(chunkSize, nalUnit.Length - offset);
            var payload = new byte[3 + size];

            payload[0] = payloadHeader0;
            payload[1] = payloadHeader1;
            payload[2] = (byte)(nalType
                | (offset == H265NalUnit.HeaderSize ? 0x80 : 0x00) // S
                | (offset + size >= nalUnit.Length ? 0x40 : 0x00)); // E

            nalUnit.Slice(offset, size).CopyTo(payload.AsSpan(3));
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
