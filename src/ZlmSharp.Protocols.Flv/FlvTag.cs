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

    public const byte AudioCodecAac = 10;

    /// <summary>FLV header 固定 9 字节；后面紧跟 PreviousTagSize0(4)。</summary>
    public const int HeaderSize = 9;

    public const int TagHeaderSize = 11;
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
