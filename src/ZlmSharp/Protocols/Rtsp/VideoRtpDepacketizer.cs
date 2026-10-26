using System.Buffers;
using System.Buffers.Binary;
using ZlmSharp.Codecs;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>视频 RTP 解包器（H.264 RFC 6184 / H.265 RFC 7798 共用）。</summary>
public interface IVideoRtpDepacketizer
{
    /// <summary>
    /// 投递一个 RTP 负载；<paramref name="marker"/> 为 true 且访问单元完整时输出 Annex-B 数据。
    /// </summary>
    bool Push(ReadOnlySpan<byte> payload, bool marker, out byte[]? accessUnit, out bool keyFrame);

    /// <summary>丢弃半包（切换 SSRC / 重连时调用）。</summary>
    void Reset();
}

/// <summary>
/// H.265 RTP 解包（RFC 7798）：单 NAL 单元包 / AP（聚合包）/ FU（分片），
/// marker 表示一个访问单元结束。输出 Annex-B，交给 <see cref="ZlmSharp.Core.MediaSource"/>。
/// </summary>
public sealed class H265RtpDepacketizer : IVideoRtpDepacketizer
{
    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    private readonly ArrayBufferWriter<byte> _accessUnit = new(64 * 1024);
    private readonly ArrayBufferWriter<byte> _fragment = new(4096);
    private bool _keyFrame;

    public bool Push(ReadOnlySpan<byte> payload, bool marker, out byte[]? accessUnit, out bool keyFrame)
    {
        accessUnit = null;
        keyFrame = false;

        if (payload.Length < 3 || (payload[0] & 0x80) != 0)
        {
            return false;
        }

        var type = (payload[0] >> 1) & 0x3F;
        switch (type)
        {
            case 48: // AP
                if (!AppendAggregationPacket(payload))
                {
                    Reset();
                    return false;
                }

                break;

            case 49: // FU
                AppendFragmentationUnit(payload);
                break;

            case <= 47:
                AppendNal(payload);
                break;

            default:
                // PACI 等其它类型不支持
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

    public void Reset()
    {
        _accessUnit.Clear();
        _fragment.Clear();
        _keyFrame = false;
    }

    private void AppendNal(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < H265NalUnit.HeaderSize)
        {
            return;
        }

        _accessUnit.Write(StartCode);
        _accessUnit.Write(nal);

        var type = (H265NalUnitType)((nal[0] >> 1) & 0x3F);
        _keyFrame |= H265NalUnit.IsKeyFrame(type);
    }

    private bool AppendAggregationPacket(ReadOnlySpan<byte> payload)
    {
        var offset = 2; // payload header
        while (offset + 2 <= payload.Length)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
            offset += 2;
            if (length < H265NalUnit.HeaderSize || offset + length > payload.Length)
            {
                return false;
            }

            AppendNal(payload.Slice(offset, length));
            offset += length;
        }

        return offset == payload.Length;
    }

    private void AppendFragmentationUnit(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            return;
        }

        var fuHeader = payload[2];
        var start = (fuHeader & 0x80) != 0;
        var end = (fuHeader & 0x40) != 0;
        var fuType = (byte)(fuHeader & 0x3F);

        if (start)
        {
            _fragment.Clear();
            // 用 FU 里的原始类型还原 2 字节 NAL 头：F + type + layerId(6) + tid(3)
            _fragment.Write([(byte)((payload[0] & 0x81) | (fuType << 1)), payload[1]]);
            _fragment.Write(payload[3..]);
            return;
        }

        if (_fragment.WrittenCount == 0)
        {
            // 丢了起始分片，等下一个 S
            return;
        }

        _fragment.Write(payload[3..]);
        if (end)
        {
            AppendNal(_fragment.WrittenSpan);
            _fragment.Clear();
        }
    }
}
