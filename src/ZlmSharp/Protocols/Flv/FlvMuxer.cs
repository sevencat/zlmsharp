using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using ZlmSharp.Codecs;
using ZlmSharp.Core;

namespace ZlmSharp.Protocols.Flv;

/// <summary>onMetaData 用的流信息。</summary>
public sealed record FlvMetadata
{
    public int Width { get; init; }

    public int Height { get; init; }

    public double FrameRate { get; init; }

    public string Encoder { get; init; } = "zlmsharp";

    public double Duration { get; init; }
}

/// <summary>
/// FLV 封装（HTTP-FLV / WS-FLV / RTMP 共用）：
/// 输出 FLV header、onMetaData、AVC sequence header 与视频 tag。
/// 输入帧约定为 <b>Annex-B</b> H.264；只有视频（一期不做音频）。
/// </summary>
public sealed class FlvMuxer
{
    private readonly H264Config _config;
    private readonly FlvMetadata _metadata;
    private readonly byte[]? _audioConfig;
    private readonly int _audioSampleRate;
    private readonly int _audioChannels;
    private readonly ArrayBufferWriter<byte> _scratch = new(64 * 1024);
    private readonly ArrayBufferWriter<byte> _body = new(256 * 1024);

    private long? _baseDts;

    /// <param name="audioConfig">AAC AudioSpecificConfig；为空表示只封装视频。</param>
    public FlvMuxer(
        H264Config config,
        FlvMetadata? metadata = null,
        ReadOnlyMemory<byte> audioConfig = default,
        int audioSampleRate = 0,
        int audioChannels = 0)
    {
        _config = config;
        _metadata = metadata ?? new FlvMetadata();
        _audioConfig = audioConfig.Length > 0 ? audioConfig.ToArray() : null;
        _audioSampleRate = audioSampleRate;
        _audioChannels = audioChannels;
    }

    public long? BaseDts => _baseDts;

    public bool HasAudio => _audioConfig is { Length: > 0 };

    /// <summary>FLV header（9 字节）+ PreviousTagSize0（4 字节）。</summary>
    public byte[] BuildHeader()
    {
        var header = new byte[13];
        header[0] = (byte)'F';
        header[1] = (byte)'L';
        header[2] = (byte)'V';
        header[3] = 1;
        header[4] = (byte)(0x01 | (HasAudio ? 0x04 : 0x00)); // flags: bit0 视频、bit2 音频
        header[5] = 0;
        header[6] = 0;
        header[7] = 0;
        header[8] = 9; // dataOffset
        // header[9..13] = PreviousTagSize0 = 0
        return header;
    }

    /// <summary>script tag 的 body（onMetaData AMF0），RTMP 播放时作为 data 消息发送。</summary>
    public byte[] BuildMetadataBody()
    {
        _scratch.Clear();
        var writer = _scratch;

        WriteAmfString(writer, "onMetaData");
        WriteAmfEcmaArrayHeader(writer, HasAudio ? 10 : 6);

        WriteAmfPropertyName(writer, "duration");
        WriteAmfNumber(writer, _metadata.Duration);
        WriteAmfPropertyName(writer, "width");
        WriteAmfNumber(writer, _metadata.Width);
        WriteAmfPropertyName(writer, "height");
        WriteAmfNumber(writer, _metadata.Height);
        WriteAmfPropertyName(writer, "videocodecid");
        WriteAmfNumber(writer, FlvConstants.VideoCodecAvc);
        WriteAmfPropertyName(writer, "framerate");
        WriteAmfNumber(writer, _metadata.FrameRate <= 0 ? 25 : _metadata.FrameRate);

        if (HasAudio)
        {
            WriteAmfPropertyName(writer, "audiocodecid");
            WriteAmfNumber(writer, FlvConstants.AudioCodecAac);
            WriteAmfPropertyName(writer, "audiosamplerate");
            WriteAmfNumber(writer, _audioSampleRate);
            WriteAmfPropertyName(writer, "audiosamplesize");
            WriteAmfNumber(writer, 16);
            WriteAmfPropertyName(writer, "stereo");
            WriteAmfNumber(writer, _audioChannels > 1 ? 1 : 0);
        }

        WriteAmfPropertyName(writer, "encoder");
        WriteAmfString(writer, _metadata.Encoder);

        WriteAmfObjectEnd(writer);

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>script tag：onMetaData。</summary>
    public byte[] BuildMetadataTag() => BuildTag(FlvTagType.Script, 0, BuildMetadataBody());

    /// <summary>video tag 的 body：AVCDecoderConfigurationRecord（AVCPacketType=0），RTMP 播放直接复用。</summary>
    public byte[] BuildAvcConfigBody()
    {
        var record = _config.BuildAvcConfigRecord();
        var body = new byte[5 + record.Length];
        body[0] = 0x17; // keyframe + AVC
        body[1] = 0;    // AVC sequence header
        // body[2..5] = composition time = 0
        record.CopyTo(body, 5);
        return body;
    }

    /// <summary>video tag：AVCDecoderConfigurationRecord（AVCPacketType=0）。</summary>
    public byte[] BuildAvcConfigTag() => BuildTag(FlvTagType.Video, 0, BuildAvcConfigBody());

    /// <summary>audio tag 的 body：AAC sequence header（AACPacketType=0）。</summary>
    public byte[] BuildAudioConfigBody()
    {
        if (_audioConfig is null)
        {
            return [];
        }

        var body = new byte[2 + _audioConfig.Length];
        body[0] = 0xAF; // AAC / 44kHz / 16bit / stereo
        body[1] = 0;    // AAC sequence header
        _audioConfig.CopyTo(body, 2);
        return body;
    }

    /// <summary>audio tag：AAC sequence header。</summary>
    public byte[] BuildAudioConfigTag()
    {
        var body = BuildAudioConfigBody();
        return body.Length == 0 ? [] : BuildTag(FlvTagType.Audio, 0, body);
    }

    /// <summary>写入一帧 AAC 的 body（2 字节 FLV 音频头 + 原始数据），RTMP 推流复用。</summary>
    public int WriteAudioBody(MediaFrame frame, IBufferWriter<byte> writer)
    {
        if (frame.TrackType != TrackType.Audio || frame.IsConfig || frame.Length == 0 || !HasAudio)
        {
            return 0;
        }

        var bodySize = 2 + frame.Length;
        var span = writer.GetSpan(bodySize);
        span[0] = 0xAF;
        span[1] = 1; // AAC raw
        frame.Data.Span.CopyTo(span[2..]);
        writer.Advance(bodySize);
        return bodySize;
    }

    /// <summary>写入一帧 AAC 原始数据（AACPacketType=1），返回写入字节数。</summary>
    public int WriteAudioTag(MediaFrame frame, IBufferWriter<byte> writer)
    {
        _body.Clear();
        var bodySize = WriteAudioBody(frame, _body);
        if (bodySize == 0)
        {
            return 0;
        }

        var timestamp = TimestampOf(frame.Dts);
        var span = writer.GetSpan(FlvConstants.TagHeaderSize + bodySize + 4);
        WriteTagHeader(span, FlvTagType.Audio, bodySize, timestamp);
        _body.WrittenSpan.CopyTo(span[FlvConstants.TagHeaderSize..]);

        BinaryPrimitives.WriteUInt32BigEndian(
            span[(FlvConstants.TagHeaderSize + bodySize)..],
            (uint)(FlvConstants.TagHeaderSize + bodySize));

        writer.Advance(FlvConstants.TagHeaderSize + bodySize + 4);
        return FlvConstants.TagHeaderSize + bodySize + 4;
    }

    /// <summary>
    /// 写入一帧视频的 tag body（AVCPacketType=1，含 5 字节 FLV 视频头），返回写入字节数。
    /// RTMP 播放用它拼 video 消息 payload。配置帧返回 0。
    /// </summary>
    public int WriteVideoBody(MediaFrame frame, IBufferWriter<byte> writer)
    {
        if (frame.TrackType != TrackType.Video || frame.IsConfig)
        {
            return 0;
        }

        _scratch.Clear();
        var payloadLength = WriteAvccPayload(frame.Data.Span, _scratch);
        if (payloadLength == 0)
        {
            return 0;
        }

        var bodySize = 5 + payloadLength;
        var span = writer.GetSpan(bodySize);

        span[0] = (byte)(((frame.IsKeyFrame ? 1 : 2) << 4) | FlvConstants.VideoCodecAvc);
        span[1] = 1; // AVC NALU

        var compositionTime = (int)Math.Clamp(frame.Pts - frame.Dts, -0x800000, 0x7FFFFF);
        span[2] = (byte)(compositionTime >> 16);
        span[3] = (byte)(compositionTime >> 8);
        span[4] = (byte)compositionTime;

        _scratch.WrittenSpan.CopyTo(span[5..]);
        writer.Advance(bodySize);

        return bodySize;
    }

    /// <summary>写入一帧视频（AVCPacketType=1），返回写入字节数；配置帧由 <see cref="BuildAvcConfigTag"/> 单独发送。</summary>
    public int WriteVideoTag(MediaFrame frame, IBufferWriter<byte> writer)
    {
        _body.Clear();
        var bodySize = WriteVideoBody(frame, _body);
        if (bodySize == 0)
        {
            return 0;
        }

        var timestamp = TimestampOf(frame.Dts);
        var span = writer.GetSpan(FlvConstants.TagHeaderSize + bodySize + 4);

        WriteTagHeader(span, FlvTagType.Video, bodySize, timestamp);
        _body.WrittenSpan.CopyTo(span[FlvConstants.TagHeaderSize..]);

        BinaryPrimitives.WriteUInt32BigEndian(
            span[(FlvConstants.TagHeaderSize + bodySize)..],
            (uint)(FlvConstants.TagHeaderSize + bodySize));

        writer.Advance(FlvConstants.TagHeaderSize + bodySize + 4);

        return FlvConstants.TagHeaderSize + bodySize + 4;
    }

    /// <summary>当前帧相对首帧的时间戳（毫秒，32 位回绕）。</summary>
    public uint TimestampOf(long dts)
    {
        _baseDts ??= dts;
        var delta = dts - _baseDts.Value;
        return delta <= 0 ? 0 : (uint)(delta & 0xFFFFFFFF);
    }

    private byte[] BuildTag(FlvTagType type, uint timestamp, ReadOnlySpan<byte> body)
    {
        var tag = new byte[FlvConstants.TagHeaderSize + body.Length + 4];
        WriteTagHeader(tag, type, body.Length, timestamp);
        body.CopyTo(tag.AsSpan(FlvConstants.TagHeaderSize));
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(FlvConstants.TagHeaderSize + body.Length), (uint)(FlvConstants.TagHeaderSize + body.Length));
        return tag;
    }

    private static void WriteTagHeader(Span<byte> destination, FlvTagType type, int bodySize, uint timestamp)
    {
        destination[0] = (byte)type;
        destination[1] = (byte)(bodySize >> 16);
        destination[2] = (byte)(bodySize >> 8);
        destination[3] = (byte)bodySize;
        destination[4] = (byte)(timestamp >> 16);
        destination[5] = (byte)(timestamp >> 8);
        destination[6] = (byte)timestamp;
        destination[7] = (byte)(timestamp >> 24);
        destination[8] = 0;
        destination[9] = 0;
        destination[10] = 0;
    }

    /// <summary>Annex-B → AVCC 长度前缀样本，返回写入长度（0 表示无有效 NALU）。</summary>
    private static int WriteAvccPayload(ReadOnlySpan<byte> annexB, IBufferWriter<byte> writer)
    {
        var written = 0;
        var offset = 0;

        while (offset < annexB.Length)
        {
            if (!AnnexB.TryFindStartCode(annexB, offset, out var codeIndex, out var codeLength))
            {
                break;
            }

            var nalStart = codeIndex + codeLength;
            if (nalStart >= annexB.Length)
            {
                break;
            }

            ReadOnlySpan<byte> nalUnit;
            if (AnnexB.TryFindStartCode(annexB, nalStart, out var nextIndex, out _))
            {
                nalUnit = annexB[nalStart..nextIndex];
                offset = nextIndex;
            }
            else
            {
                nalUnit = annexB[nalStart..];
                offset = annexB.Length;
            }

            if (nalUnit.IsEmpty)
            {
                continue;
            }

            var span = writer.GetSpan(4 + nalUnit.Length);
            BinaryPrimitives.WriteUInt32BigEndian(span, (uint)nalUnit.Length);
            nalUnit.CopyTo(span[4..]);
            writer.Advance(4 + nalUnit.Length);
            written += 4 + nalUnit.Length;
        }

        return written;
    }

    private static void WriteAmfString(IBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var span = writer.GetSpan(3 + byteCount);
        span[0] = 0x02; // string
        BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)byteCount);
        Encoding.UTF8.GetBytes(value, span[3..]);
        writer.Advance(3 + byteCount);
    }

    /// <summary>对象属性名：只有 2 字节长度 + UTF8，没有 0x02 marker（与字符串值不同）。</summary>
    private static void WriteAmfPropertyName(IBufferWriter<byte> writer, string name)
    {
        var byteCount = Encoding.UTF8.GetByteCount(name);
        var span = writer.GetSpan(2 + byteCount);
        BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)byteCount);
        Encoding.UTF8.GetBytes(name, span[2..]);
        writer.Advance(2 + byteCount);
    }

    private static void WriteAmfNumber(IBufferWriter<byte> writer, double value)
    {
        var span = writer.GetSpan(9);
        span[0] = 0x00; // number
        BinaryPrimitives.WriteDoubleBigEndian(span[1..], value);
        writer.Advance(9);
    }

    private static void WriteAmfEcmaArrayHeader(IBufferWriter<byte> writer, int count)
    {
        var span = writer.GetSpan(5);
        span[0] = 0x08; // ECMA array
        BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)count);
        writer.Advance(5);
    }

    private static void WriteAmfObjectEnd(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(3);
        span[0] = 0;
        span[1] = 0;
        span[2] = 0x09;
        writer.Advance(3);
    }
}
