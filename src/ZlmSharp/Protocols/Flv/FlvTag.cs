using System.Buffers.Binary;

namespace ZlmSharp.Protocols.Flv;

public enum FlvTagType : byte
{
    Audio = 8,
    Video = 9,
    Script = 18,
}

public static class FlvConstants
{
    public const byte VideoCodecAvc = 7;

    /// <summary>HEVC（legacy FLV 编码 id，配合 hvcC sequence header 使用）。</summary>
    public const byte VideoCodecHevc = 12;

    public const byte AudioCodecAac = 10;

    /// <summary>FLV header 固定 9 字节；后面紧跟 PreviousTagSize0(4)。</summary>
    public const int HeaderSize = 9;

    public const int TagHeaderSize = 11;

    /// <summary>Enhanced RTMP：视频 tag 首字节 bit7 = IsExHeader，置位时低 4 位是 PacketType。</summary>
    public const byte VideoExHeaderFlag = 0x80;

    /// <summary>帧类型掩码（bit6..4）：1 key / 2 inter，legacy 与增强头一致。</summary>
    public const byte VideoFrameTypeMask = 0x70;

    public const byte VideoCodecIdMask = 0x0F;
}

/// <summary>Enhanced RTMP 视频包类型（视频 tag 首字节低 4 位）。</summary>
public enum FlvVideoPacketType : byte
{
    /// <summary>编解码器配置（avcC / hvcC 等）。</summary>
    SequenceStart = 0,

    /// <summary>编码帧，header 后跟 3 字节 composition time。</summary>
    CodedFrames = 1,

    SequenceEnd = 2,

    /// <summary>编码帧，省略 3 字节 composition time（CTS 视为 0）。</summary>
    CodedFramesX = 3,

    /// <summary>HDR 等视频元数据（AMF0），不是图像帧。</summary>
    Metadata = 4,

    Mpeg2TsSequenceStart = 5,

    /// <summary>多轨：真正的 packet type 在下一个字节的低 4 位。</summary>
    Multitrack = 6,

    /// <summary>ModEx（纳秒时间戳等扩展），暂不支持。</summary>
    ModEx = 7,
}

/// <summary>Enhanced RTMP 的编解码器 FourCC（大端 4 字节，规范里原样 ASCII）。</summary>
public static class FlvFourCc
{
    public const uint Avc1 = 0x61766331; // 'avc1'

    public const uint Hvc1 = 0x68766331; // 'hvc1'
}

/// <summary>
/// 视频 tag body 的头（同时支持 legacy codec id 与 Enhanced RTMP）。
/// legacy：<c>[FrameType|CodecId][PacketType][CTS:3]</c>；
/// enhanced：<c>[0x80|FrameType|PacketType][FourCC:4][CTS:3?]</c>，只有 CodedFrames 带 CTS。
/// </summary>
public readonly record struct FlvVideoTagHeader
{
    private FlvVideoTagHeader(
        int frameType,
        bool enhanced,
        byte codecId,
        FlvVideoPacketType packetType,
        uint fourCc,
        int compositionTime,
        int payloadOffset)
    {
        FrameType = frameType;
        IsEnhanced = enhanced;
        CodecId = codecId;
        PacketType = packetType;
        FourCc = fourCc;
        CompositionTime = compositionTime;
        PayloadOffset = payloadOffset;
    }

    public int FrameType { get; }

    public bool IsEnhanced { get; }

    /// <summary>legacy 编码 id（7=AVC / 12=HEVC）；增强头时为 0。</summary>
    public byte CodecId { get; }

    /// <summary>legacy 下即 AVCPacketType（0 配置 / 1 帧），增强下即 <see cref="FlvVideoPacketType"/>。</summary>
    public FlvVideoPacketType PacketType { get; }

    /// <summary>增强头里的 FourCC；legacy 时为 0。</summary>
    public uint FourCc { get; }

    public int CompositionTime { get; }

    /// <summary>码流数据相对 tag body 的偏移。</summary>
    public int PayloadOffset { get; }

    public bool IsHevc => IsEnhanced
        ? FourCc == FlvFourCc.Hvc1
        : CodecId == FlvConstants.VideoCodecHevc;

    public bool IsAvc => IsEnhanced
        ? FourCc == FlvFourCc.Avc1
        : CodecId == FlvConstants.VideoCodecAvc;

    public bool IsSequenceHeader => PacketType == FlvVideoPacketType.SequenceStart;

    public bool IsCodedFrames => PacketType is FlvVideoPacketType.CodedFrames or FlvVideoPacketType.CodedFramesX;

    /// <summary>解析视频 tag body 头；数据不足或不是 H.264/H.265 时返回 false。</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out FlvVideoTagHeader header)
    {
        header = default;
        if (payload.IsEmpty)
        {
            return false;
        }

        var first = payload[0];
        var frameType = (first & FlvConstants.VideoFrameTypeMask) >> 4;

        if ((first & FlvConstants.VideoExHeaderFlag) != 0)
        {
            // 增强头：首字节 + FourCC(4) + [CodedFrames 才有 CTS(3)]
            if (payload.Length < 5)
            {
                return false;
            }

            var fourCc = BinaryPrimitives.ReadUInt32BigEndian(payload[1..]);
            var packetType = (FlvVideoPacketType)(first & FlvConstants.VideoCodecIdMask);
            var offset = 5;
            var compositionTime = 0;

            if (packetType == FlvVideoPacketType.CodedFrames)
            {
                if (payload.Length < 8)
                {
                    return false;
                }

                compositionTime = ReadInt24(payload, 5);
                offset = 8;
            }

            header = new FlvVideoTagHeader(frameType, true, 0, packetType, fourCc, compositionTime, offset);
            return true;
        }

        // legacy：首字节 + AVCPacketType(1) + CTS(3)，只有 AVC/HEVC 是这个 5 字节头
        var codecId = (byte)(first & FlvConstants.VideoCodecIdMask);
        if (codecId != FlvConstants.VideoCodecAvc && codecId != FlvConstants.VideoCodecHevc)
        {
            return false;
        }

        if (payload.Length < 5)
        {
            return false;
        }

        header = new FlvVideoTagHeader(
            frameType,
            false,
            codecId,
            (FlvVideoPacketType)payload[1],
            0,
            ReadInt24(payload, 2),
            5);

        return true;
    }

    private static int ReadInt24(ReadOnlySpan<byte> buffer, int offset)
    {
        var value = (buffer[offset] << 16) | (buffer[offset + 1] << 8) | buffer[offset + 2];
        return (value & 0x800000) != 0 ? value - 0x1000000 : value;
    }
}

/// <summary>解析出来的 FLV tag（测试与后续 RTMP 复用）。</summary>
public sealed record FlvTag(FlvTagType Type, uint Timestamp, byte[] Body)
{
    /// <summary>AVC 视频 tag 的帧类型（1 key / 2 inter）。</summary>
    public int VideoFrameType => Type == FlvTagType.Video && Body.Length > 0 ? (Body[0] >> 4) & 0x0F : 0;

    public int AvcPacketType => Type == FlvTagType.Video && Body.Length > 1 ? Body[1] : -1;

    public bool IsKeyFrame => VideoFrameType == 1;
}

/// <summary>把 FLV 字节流切成 tag（离线场景：测试、MP4/FLV 文件分析）。</summary>
public static class FlvTagParser
{
    public static List<FlvTag> ParseAll(ReadOnlySpan<byte> data)
    {
        var result = new List<FlvTag>();

        if (data.Length >= 3 && data[0] == 'F' && data[1] == 'L' && data[2] == 'V')
        {
            if (data.Length < FlvConstants.HeaderSize + 4)
            {
                return result;
            }

            var offset = FlvConstants.HeaderSize;
            var previousTagSize = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            offset += 4 + previousTagSize;
            data = data[offset..];
        }

        while (data.Length >= FlvConstants.TagHeaderSize)
        {
            var type = (FlvTagType)data[0];
            var size = (data[1] << 16) | (data[2] << 8) | data[3];
            var timestamp = ((uint)data[7] << 24) | ((uint)data[4] << 16) | ((uint)data[5] << 8) | data[6];

            if (data.Length < FlvConstants.TagHeaderSize + size + 4)
            {
                break;
            }

            var body = data.Slice(FlvConstants.TagHeaderSize, size).ToArray();
            result.Add(new FlvTag(type, timestamp, body));

            var consumed = FlvConstants.TagHeaderSize + size + 4;
            data = data[consumed..];
        }

        return result;
    }
}
