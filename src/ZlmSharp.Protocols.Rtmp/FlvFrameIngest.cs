using System.Buffers;
using ZlmSharp.Codecs;
using ZlmSharp.Core;

namespace ZlmSharp.Protocols.Rtmp;

/// <summary>
/// FLV 音视频负载（RTMP Video/Audio 消息体）→ <see cref="MediaSource"/> 帧。
/// 推流（服务端接收）与拉流代理（客户端接收）共用，避免两份 AVC / AAC 解析。
/// </summary>
public sealed class FlvFrameIngest
{
    private static readonly int[] AacSampleRates =
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    private readonly MediaSource _source;
    private readonly RtmpLogHandler? _log;

    private H264Config? _h264;
    private bool _warnedUnsupportedVideo;

    public FlvFrameIngest(MediaSource source, RtmpLogHandler? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _log = log;
    }

    /// <summary>当前 H.264 参数集（收到 AVC sequence header 后才有值）。</summary>
    public H264Config? H264 => _h264;

    public void Video(ReadOnlySpan<byte> payload, long dts)
    {
        if (payload.Length < 5)
        {
            return;
        }

        var frameType = (payload[0] >> 4) & 0x0F;
        var codecId = payload[0] & 0x0F;
        if (codecId != RtmpConstants.FlvCodecAvc)
        {
            if (!_warnedUnsupportedVideo)
            {
                _warnedUnsupportedVideo = true;
                Log(RtmpLogLevel.Warning, $"暂不支持的 RTMP 视频编码 id={codecId}（仅支持 H.264）");
            }

            return;
        }

        var packetType = payload[1];
        var body = payload[5..];

        if (packetType == 0)
        {
            if (!H264Avcc.TryParseConfigRecord(body, out var config, out var error) || config is null)
            {
                Log(RtmpLogLevel.Warning, $"RTMP AVC 配置解析失败: {error}");
                return;
            }

            _h264 = config;
            _source.Video.SetCodec(CodecId.H264);
            _source.Video.SetH264Config(config.Sps, config.Pps);
            if (H264SpsParser.TryParse(config.Sps, out var info, out _) && info is not null)
            {
                _source.Video.SetVideoSize(info.Width, info.Height);
            }

            var parameterSets = config.BuildAnnexBParameterSets();
            _source.AddFrame(MediaFrame.Create(
                parameterSets,
                TrackType.Video,
                CodecId.H264,
                dts,
                dts,
                FrameFlags.Config | FrameFlags.Key));

            Log(RtmpLogLevel.Info,
                $"RTMP 收到 AVC 配置 {_source.Video.Width}x{_source.Video.Height} sps={config.Sps.Length} pps={config.Pps.Length}");
            return;
        }

        if (packetType != 1)
        {
            return;
        }

        var lengthSize = _h264?.LengthSize ?? 4;
        var writer = new ArrayBufferWriter<byte>(body.Length + 32);
        try
        {
            if (H264Avcc.ToAnnexB(body, lengthSize, writer) == 0)
            {
                return;
            }
        }
        catch (InvalidDataException exception)
        {
            Log(RtmpLogLevel.Warning, $"RTMP AVC 样本非法: {exception.Message}");
            return;
        }

        var compositionTime = ReadInt24(payload, 2);
        var isKeyFrame = frameType == 1 || H264Avcc.ContainsIdr(body, lengthSize);
        _source.AddFrame(MediaFrame.Create(
            writer.WrittenSpan,
            TrackType.Video,
            CodecId.H264,
            dts,
            dts + compositionTime,
            isKeyFrame ? FrameFlags.Key : FrameFlags.None));
    }

    public void Audio(ReadOnlySpan<byte> payload, long dts)
    {
        if (payload.Length < 2)
        {
            return;
        }

        var soundFormat = (payload[0] >> 4) & 0x0F;
        if (soundFormat != RtmpConstants.FlvSoundFormatAac)
        {
            return;
        }

        var packetType = payload[1];
        var body = payload[2..];

        if (packetType == 0)
        {
            var (sampleRate, channels) = ParseAudioSpecificConfig(body);
            _source.Audio.SetCodec(CodecId.Aac);
            if (sampleRate > 0)
            {
                _source.Audio.SetAudioInfo(sampleRate, channels);
            }

            if (!body.IsEmpty)
            {
                _source.AddFrame(MediaFrame.Create(
                    body,
                    TrackType.Audio,
                    CodecId.Aac,
                    dts,
                    dts,
                    FrameFlags.Config));
            }

            return;
        }

        if (packetType == 1 && !body.IsEmpty)
        {
            _source.Audio.SetCodec(CodecId.Aac);
            _source.AddFrame(MediaFrame.Create(body, TrackType.Audio, CodecId.Aac, dts, dts));
        }
    }

    private static (int SampleRate, int Channels) ParseAudioSpecificConfig(ReadOnlySpan<byte> asc)
    {
        if (asc.Length < 2)
        {
            return (0, 0);
        }

        var frequencyIndex = ((asc[0] & 0x07) << 1) | (asc[1] >> 7);
        var channelConfig = (asc[1] >> 3) & 0x0F;
        var sampleRate = frequencyIndex < AacSampleRates.Length ? AacSampleRates[frequencyIndex] : 0;

        return (sampleRate, channelConfig);
    }

    private static int ReadInt24(ReadOnlySpan<byte> buffer, int offset)
    {
        var value = (buffer[offset] << 16) | (buffer[offset + 1] << 8) | buffer[offset + 2];
        return (value & 0x800000) != 0 ? value - 0x1000000 : value;
    }

    private void Log(RtmpLogLevel level, string message) => _log?.Invoke(level, message);
}
