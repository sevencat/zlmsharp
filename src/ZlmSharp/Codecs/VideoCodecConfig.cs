using ZlmSharp.Core;

namespace ZlmSharp.Codecs;

/// <summary>
/// 视频参数集统一视图：H.264（SPS+PPS）或 H.265（VPS+SPS+PPS）。
/// 封装层（FLV / MP4 / TS / RTSP）只依赖这里，避免在每个 muxer 里写 codec 分支。
/// </summary>
public sealed class VideoCodecConfig
{
    private VideoCodecConfig(H264Config? h264, H265Config? h265)
    {
        H264 = h264;
        H265 = h265;
    }

    public H264Config? H264 { get; }

    public H265Config? H265 { get; }

    public CodecId Codec => H265 is not null ? CodecId.H265 : CodecId.H264;

    public bool IsHevc => H265 is not null;

    /// <summary>AVCC 长度前缀宽度（HEVC 固定 4）。</summary>
    public int LengthSize => H265?.LengthSize ?? H264?.LengthSize ?? 4;

    public static VideoCodecConfig FromH264(H264Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new VideoCodecConfig(config, null);
    }

    public static VideoCodecConfig FromH265(H265Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new VideoCodecConfig(null, config);
    }

    /// <summary>FLV 的 AVCDecoderConfigurationRecord / hvcC。</summary>
    public byte[] BuildConfigRecord() =>
        H265?.BuildHvcConfigRecord() ?? H264!.BuildAvcConfigRecord();

    /// <summary>Annex-B 参数集（GOP 秒开用的配置帧内容）。</summary>
    public byte[] BuildAnnexBParameterSets() =>
        H265?.BuildAnnexBParameterSets() ?? H264!.BuildAnnexBParameterSets();
}

/// <summary>
/// 从 <see cref="MediaSource"/> 取视频参数集：优先用轨道缓存，否则扫描 GOP 里的配置帧并回填。
/// 同时兼容 H.264 与 H.265，供所有播放/转推/录像路径使用。
/// </summary>
public static class VideoCodecConfigResolver
{
    public static VideoCodecConfig? Resolve(MediaSource source)
    {
        var track = source.Video;

        if (track.CodecId == CodecId.H265)
        {
            if (track.Vps is { Length: > 0 } vps && track.Sps is { Length: > 0 } sps && track.Pps is { Length: > 0 } pps)
            {
                return VideoCodecConfig.FromH265(new H265Config { Vps = vps, Sps = sps, Pps = pps });
            }
        }
        else if (track.Sps is { Length: > 0 } h264Sps && track.Pps is { Length: > 0 } h264Pps)
        {
            return VideoCodecConfig.FromH264(new H264Config { Sps = h264Sps, Pps = h264Pps });
        }

        foreach (var frame in source.SnapshotGop())
        {
            try
            {
                if (frame.TrackType != TrackType.Video || !frame.IsConfig)
                {
                    continue;
                }

                var config = TryReadParameterSets(frame.Data.Span, track);
                if (config is not null)
                {
                    return config;
                }
            }
            finally
            {
                frame.Dispose();
            }
        }

        return null;
    }

    /// <summary>轮询等待参数集就绪；超时返回 null。</summary>
    public static async Task<VideoCodecConfig?> WaitAsync(
        MediaSource source,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);

        while (true)
        {
            var config = Resolve(source);
            if (config is not null)
            {
                return config;
            }

            if (timeoutMs <= 0 || Environment.TickCount64 >= deadline)
            {
                return null;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static VideoCodecConfig? TryReadParameterSets(ReadOnlySpan<byte> annexB, MediaTrack track)
    {
        byte[]? vps = null;
        byte[]? sps = null;
        byte[]? pps = null;

        foreach (var nal in AnnexB.Split(annexB))
        {
            if (!H265NalUnit.TryReadHeader(nal, out var type, out _))
            {
                continue;
            }

            switch (type)
            {
                case H265NalUnitType.Vps:
                    vps ??= nal;
                    break;
                case H265NalUnitType.Sps:
                    sps ??= nal;
                    break;
                case H265NalUnitType.Pps:
                    pps ??= nal;
                    break;
            }
        }

        if (vps is not null && sps is not null && pps is not null)
        {
            track.SetCodec(CodecId.H265);
            track.SetH265Config(vps, sps, pps);
            if (H265SpsParser.TryParse(sps, out var info, out _) && info is not null)
            {
                track.SetVideoSize(info.Width, info.Height);
            }

            return VideoCodecConfig.FromH265(new H265Config { Vps = vps, Sps = sps, Pps = pps });
        }

        var h264 = H264ConfigResolver.FromAnnexB(annexB, track);
        return h264 is null ? null : VideoCodecConfig.FromH264(h264);
    }
}
