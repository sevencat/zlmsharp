using ZlmSharp.Core;

namespace ZlmSharp.Codecs;

/// <summary>
/// 从 <see cref="MediaSource"/> 取 H.264 参数集：优先用轨道缓存，
/// 否则扫描 GOP 里的配置帧（Annex-B SPS/PPS）并回填轨道元信息。
/// HTTP-FLV、RTMP、RTSP 播放共用。
/// </summary>
public static class H264ConfigResolver
{
    public static H264Config? Resolve(MediaSource source)
    {
        var track = source.Video;
        if (track.Sps is { Length: > 0 } sps && track.Pps is { Length: > 0 } pps)
        {
            return new H264Config { Sps = sps, Pps = pps };
        }

        foreach (var frame in source.SnapshotGop())
        {
            try
            {
                if (frame.TrackType != TrackType.Video || !frame.IsConfig)
                {
                    continue;
                }

                var found = FromAnnexB(frame.Data.Span, track);
                if (found is null)
                {
                    continue;
                }

                return found;
            }
            finally
            {
                frame.Dispose();
            }
        }

        return null;
    }

    /// <summary>从一帧 Annex-B 数据里提取 SPS/PPS 并回填轨道；没有完整参数集时返回 null。</summary>
    public static H264Config? FromAnnexB(ReadOnlySpan<byte> annexB, MediaTrack track)
    {
        byte[]? foundSps = null;
        byte[]? foundPps = null;

        foreach (var nal in AnnexB.Split(annexB))
        {
            if (nal.Length < 2)
            {
                continue;
            }

            switch (H264NalUnit.GetType(nal))
            {
                case H264NalUnitType.Sps:
                    foundSps ??= nal;
                    break;
                case H264NalUnitType.Pps:
                    foundPps ??= nal;
                    break;
            }
        }

        if (foundSps is null || foundPps is null)
        {
            return null;
        }

        track.SetH264Config(foundSps, foundPps);
        track.SetCodec(CodecId.H264);
        if (H264SpsParser.TryParse(foundSps, out var info, out _) && info is not null)
        {
            track.SetVideoSize(info.Width, info.Height);
        }

        return new H264Config { Sps = foundSps, Pps = foundPps };
    }

    /// <summary>轮询等待参数集就绪；超时返回 null。</summary>
    public static async Task<H264Config?> WaitAsync(
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
}
