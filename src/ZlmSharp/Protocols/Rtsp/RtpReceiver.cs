using System.Buffers;
using System.Buffers.Binary;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>RTP 包头（RFC 3550）解析结果。</summary>
public readonly record struct RtpPacketHeader(
    int PayloadType,
    bool Marker,
    ushort SequenceNumber,
    uint Timestamp,
    uint Ssrc,
    int PayloadOffset,
    int PayloadLength)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out RtpPacketHeader header)
    {
        header = default;

        if (packet.Length < 12 || (packet[0] >> 6) != 2)
        {
            return false;
        }

        var padding = (packet[0] & 0x20) != 0;
        var extension = (packet[0] & 0x10) != 0;
        var csrcCount = packet[0] & 0x0F;
        var offset = 12 + (4 * csrcCount);
        if (offset > packet.Length)
        {
            return false;
        }

        if (extension)
        {
            if (offset + 4 > packet.Length)
            {
                return false;
            }

            var extensionLength = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            offset += 4 + (4 * extensionLength);
            if (offset > packet.Length)
            {
                return false;
            }
        }

        var end = packet.Length;
        if (padding)
        {
            var paddingLength = packet[^1];
            if (paddingLength == 0 || offset + paddingLength > end)
            {
                return false;
            }

            end -= paddingLength;
        }

        if (end <= offset)
        {
            return false;
        }

        header = new RtpPacketHeader(
            packet[1] & 0x7F,
            (packet[1] & 0x80) != 0,
            BinaryPrimitives.ReadUInt16BigEndian(packet[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet[8..]),
            offset,
            end - offset);

        return true;
    }
}

/// <summary>
/// H.264 RTP 解包（RFC 6184）：单 NALU / STAP-A / FU-A，marker 表示一个访问单元结束。
/// 输出为 Annex-B（4 字节起始码），供 <see cref="ZlmSharp.Core.MediaSource"/> 直接使用。
/// </summary>
public sealed class H264RtpDepacketizer : IVideoRtpDepacketizer
{
    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    private readonly ArrayBufferWriter<byte> _accessUnit = new(64 * 1024);
    private readonly ArrayBufferWriter<byte> _fragment = new(4096);
    private bool _keyFrame;

    /// <summary>
    /// 投递一个 RTP 负载；<paramref name="marker"/> 为 true 且访问单元完整时输出 Annex-B 数据。
    /// </summary>
    public bool Push(ReadOnlySpan<byte> payload, bool marker, out byte[]? accessUnit, out bool keyFrame)
    {
        accessUnit = null;
        keyFrame = false;

        if (payload.IsEmpty)
        {
            return false;
        }

        var nalType = payload[0] & 0x1F;
        switch (nalType)
        {
            case >= 1 and <= 23:
                AppendNal(payload);
                break;

            case 24:
                if (!AppendStapA(payload))
                {
                    Reset();
                    return false;
                }

                break;

            case 28:
                AppendFuA(payload);
                break;

            default:
                // 其它类型（STAP-B/MTAP/FU-B 等）不支持
                return false;
        }

        if (!marker || _accessUnit.WrittenCount == 0 || _fragment.WrittenCount > 0)
        {
            return false;
        }

        accessUnit = _accessUnit.WrittenSpan.ToArray();
        keyFrame = _keyFrame;
        _accessUnit.Clear();
        _keyFrame = false;
        return true;
    }

    /// <summary>丢弃半包（切换到新 SSRC / 重连时调用）。</summary>
    public void Reset()
    {
        _accessUnit.Clear();
        _fragment.Clear();
        _keyFrame = false;
    }

    private void AppendNal(ReadOnlySpan<byte> nal)
    {
        if (nal.IsEmpty)
        {
            return;
        }

        _accessUnit.Write(StartCode);
        _accessUnit.Write(nal);
        _keyFrame |= (nal[0] & 0x1F) == 5;
    }

    private bool AppendStapA(ReadOnlySpan<byte> payload)
    {
        var offset = 1;
        while (offset + 2 <= payload.Length)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
            offset += 2;
            if (length == 0 || offset + length > payload.Length)
            {
                return false;
            }

            AppendNal(payload.Slice(offset, length));
            offset += length;
        }

        return offset == payload.Length;
    }

    private void AppendFuA(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            return;
        }

        var header = payload[1];
        var start = (header & 0x80) != 0;
        var end = (header & 0x40) != 0;
        var nalType = header & 0x1F;

        if (start)
        {
            _fragment.Clear();
            _fragment.Write([(byte)((payload[0] & 0xE0) | nalType)]);
            _fragment.Write(payload[2..]);
            return;
        }

        if (_fragment.WrittenCount == 0)
        {
            // 丢了起始分片，等下一个 S
            return;
        }

        _fragment.Write(payload[2..]);
        if (end)
        {
            AppendNal(_fragment.WrittenSpan);
            _fragment.Clear();
        }
    }
}
