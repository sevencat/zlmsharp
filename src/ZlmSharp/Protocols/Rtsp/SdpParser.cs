using System.Globalization;
using ZlmSharp.Core;

namespace ZlmSharp.Protocols.Rtsp;

public enum SdpMediaKind
{
    Video,
    Audio,
}

/// <summary>ANNOUNCE SDP 里的一条媒体轨道。</summary>
public sealed record SdpMediaTrack(
    SdpMediaKind Kind,
    int PayloadType,
    int ClockRate,
    int Channels,
    string Control,
    string Encoding,
    byte[]? Sps,
    byte[]? Pps,
    byte[]? AudioSpecificConfig,
    byte[]? Vps = null)
{
    /// <summary>视频轨是否为 HEVC（rtpmap 里是 H265 或 HEVC）。</summary>
    public bool IsH265 =>
        Encoding.Equals("H265", StringComparison.OrdinalIgnoreCase) ||
        Encoding.Equals("HEVC", StringComparison.OrdinalIgnoreCase);

    public CodecId Codec => IsH265 ? CodecId.H265 : CodecId.H264;

    /// <summary>从 <c>a=control:trackID=1</c> / <c>streamid=1</c> 解析出的轨道号；没有时为 -1。</summary>
    public int TrackId
    {
        get
        {
            foreach (var prefix in new[] { "trackID=", "streamid=" })
            {
                var index = Control.LastIndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 &&
                    int.TryParse(Control[(index + prefix.Length)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    return id;
                }
            }

            return -1;
        }
    }

    public bool HasParameterSets => IsH265
        ? Vps is { Length: > 0 } && Sps is { Length: > 0 } && Pps is { Length: > 0 }
        : Sps is { Length: > 0 } && Pps is { Length: > 0 };
}

/// <summary>ANNOUNCE 的完整描述：最多一条 H.264/H.265 视频轨 + 一条 AAC 音频轨。</summary>
public sealed record SdpDescription(SdpMediaTrack? Video, SdpMediaTrack? Audio)
{
    public bool IsEmpty => Video is null && Audio is null;
}

/// <summary>只解析「H.264 视频推流」需要的最小 SDP 子集（m=video / rtpmap / fmtp / control）。</summary>
public sealed record SdpH264Track(
    int PayloadType,
    int ClockRate,
    string Control,
    byte[]? Sps,
    byte[]? Pps)
{
    public bool HasParameterSets => Sps is { Length: > 0 } && Pps is { Length: > 0 };

    public int TrackId => 0;
}

/// <summary>
/// SDP 解析：支持 H.264 视频轨与 <c>MPEG4-GENERIC</c>（AAC）音频轨。
/// 只取第一条视频轨和第一条音频轨，其它轨道忽略。
/// </summary>
public static class SdpParser
{
    /// <summary>兼容旧接口：只取 H.264 视频轨。</summary>
    public static bool TryParseH264(string? sdp, out SdpH264Track? track, out string error)
    {
        track = null;

        if (!TryParse(sdp, out var description, out error))
        {
            return false;
        }

        if (description!.Video is not { } video || video.IsH265)
        {
            error = "SDP 中没有 H.264 视频轨道";
            return false;
        }

        track = new SdpH264Track(video.PayloadType, video.ClockRate, video.Control, video.Sps, video.Pps);
        return true;
    }

    /// <summary>解析视频 + 音频轨道；至少要有其中一条。</summary>
    public static bool TryParse(string? sdp, out SdpDescription? description, out string error)
    {
        description = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sdp))
        {
            error = "SDP 为空";
            return false;
        }

        string? sessionControl = null;
        SdpMediaTrack? video = null;
        SdpMediaTrack? audio = null;

        // 当前 m= 段的状态
        SdpMediaKind? kind = null;
        var payloadType = 0;
        string? encoding = null;
        var clockRate = 0;
        var channels = 0;
        string? fmtp = null;
        string? mediaControl = null;
        string? formatError = null;

        void FlushSegment()
        {
            if (kind is null)
            {
                return;
            }

            var control = mediaControl ?? sessionControl ?? string.Empty;
            if (kind == SdpMediaKind.Video)
            {
                if (video is null && !string.IsNullOrEmpty(encoding) &&
                    (encoding.Equals("H264", StringComparison.OrdinalIgnoreCase) ||
                     encoding.Equals("H265", StringComparison.OrdinalIgnoreCase) ||
                     encoding.Equals("HEVC", StringComparison.OrdinalIgnoreCase)))
                {
                    var isHevc = !encoding.Equals("H264", StringComparison.OrdinalIgnoreCase);
                    byte[]? sps = null;
                    byte[]? pps = null;
                    byte[]? vps = null;

                    if (!string.IsNullOrEmpty(fmtp))
                    {
                        var parsed = isHevc
                            ? TryParseH265ParameterSets(fmtp, out vps, out sps, out pps)
                            : TryParseParameterSets(fmtp, out sps, out pps);

                        if (!parsed)
                        {
                            formatError = isHevc
                                ? "sprop-vps/sps/pps 不是合法 base64"
                                : "sprop-parameter-sets 不是合法 base64";
                            return;
                        }
                    }

                    video = new SdpMediaTrack(
                        SdpMediaKind.Video,
                        payloadType,
                        clockRate > 0 ? clockRate : 90_000,
                        0,
                        control,
                        encoding,
                        sps,
                        pps,
                        null,
                        vps);
                }
            }
            else if (audio is null && !string.IsNullOrEmpty(encoding) && IsAacEncoding(encoding))
            {
                byte[]? asc = null;
                if (!string.IsNullOrEmpty(fmtp))
                {
                    TryParseAudioConfig(fmtp, out asc);
                }

                audio = new SdpMediaTrack(
                    SdpMediaKind.Audio,
                    payloadType,
                    clockRate > 0 ? clockRate : 44_100,
                    channels > 0 ? channels : 2,
                    control,
                    encoding,
                    null,
                    null,
                    asc);
            }

            kind = null;
            encoding = null;
            clockRate = 0;
            channels = 0;
            fmtp = null;
            mediaControl = null;
        }

        foreach (var raw in sdp.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                FlushSegment();

                var segments = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                kind = segments[0].Equals("m=video", StringComparison.OrdinalIgnoreCase)
                    ? SdpMediaKind.Video
                    : segments[0].Equals("m=audio", StringComparison.OrdinalIgnoreCase)
                        ? SdpMediaKind.Audio
                        : null;

                payloadType = segments.Length > 0 &&
                              int.TryParse(segments[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;

                continue;
            }

            if (kind is null)
            {
                if (sessionControl is null && line.StartsWith("a=control:", StringComparison.OrdinalIgnoreCase))
                {
                    sessionControl = line["a=control:".Length..].Trim();
                }

                continue;
            }

            if (line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase))
            {
                ParseRtpMap(line, ref payloadType, ref encoding, ref clockRate, ref channels);
            }
            else if (line.StartsWith("a=fmtp:", StringComparison.OrdinalIgnoreCase))
            {
                var (pt, value) = ParseAttributeWithPayload(line, "a=fmtp:");
                if (pt is null || pt == payloadType)
                {
                    fmtp = value;
                }
            }
            else if (line.StartsWith("a=control:", StringComparison.OrdinalIgnoreCase))
            {
                mediaControl = line["a=control:".Length..].Trim();
            }
        }

        FlushSegment();

        if (formatError is not null)
        {
            error = formatError;
            return false;
        }

        if (video is null && audio is null)
        {
            error = "SDP 中没有可用的 H.264 / H.265 / AAC 轨道";
            return false;
        }

        description = new SdpDescription(video, audio);
        return true;
    }

    private static bool IsAacEncoding(string encoding) =>
        encoding.Equals("MPEG4-GENERIC", StringComparison.OrdinalIgnoreCase) ||
        encoding.Equals("AAC", StringComparison.OrdinalIgnoreCase) ||
        encoding.Equals("MP4A-LATM", StringComparison.OrdinalIgnoreCase);

    private static void ParseRtpMap(
        string line,
        ref int payloadType,
        ref string? encoding,
        ref int clockRate,
        ref int channels)
    {
        var (pt, value) = ParseAttributeWithPayload(line, "a=rtpmap:");
        if (pt is null || value is null)
        {
            return;
        }

        // 允许 rtpmap 出现在 m= 之前（少数实现），此时以 rtpmap 的 PT 为准
        if (payloadType != 0 && pt != payloadType)
        {
            return;
        }

        var parts = value.Split('/');
        var name = parts[0].Trim();
        if (parts.Length > 1 &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate))
        {
            clockRate = rate;
        }

        if (parts.Length > 2 &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            channels = count;
        }

        encoding = name;
    }

    private static (int? PayloadType, string? Value) ParseAttributeWithPayload(string line, string prefix)
    {
        var rest = line[prefix.Length..];
        var space = rest.IndexOf(' ');
        if (space <= 0)
        {
            return (null, null);
        }

        var ptText = rest[..space];
        return int.TryParse(ptText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pt)
            ? (pt, rest[(space + 1)..].Trim())
            : (null, null);
    }

    /// <summary>HEVC 的 fmtp：<c>sprop-vps=..;sprop-sps=..;sprop-pps=..</c>（各自独立 base64）。</summary>
    private static bool TryParseH265ParameterSets(
        string fmtp,
        out byte[]? vps,
        out byte[]? sps,
        out byte[]? pps)
    {
        vps = null;
        sps = null;
        pps = null;

        foreach (var item in fmtp.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = item.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = item[..equals].Trim();
            var isVps = key.Equals("sprop-vps", StringComparison.OrdinalIgnoreCase);
            var isSps = key.Equals("sprop-sps", StringComparison.OrdinalIgnoreCase);
            var isPps = key.Equals("sprop-pps", StringComparison.OrdinalIgnoreCase);
            if (!isVps && !isSps && !isPps)
            {
                continue;
            }

            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(item[(equals + 1)..].Trim());
            }
            catch (FormatException)
            {
                return false;
            }

            if (decoded.Length == 0)
            {
                continue;
            }

            if (isVps)
            {
                vps = decoded;
            }
            else if (isSps)
            {
                sps = decoded;
            }
            else
            {
                pps = decoded;
            }
        }

        return true;
    }

    private static bool TryParseParameterSets(
        string fmtp,
        out byte[]? sps,
        out byte[]? pps)
    {
        sps = null;
        pps = null;

        foreach (var item in fmtp.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = item.IndexOf('=');
            if (equals <= 0 || !item[..equals].Trim().Equals("sprop-parameter-sets", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = item[(equals + 1)..].Trim();
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                byte[] decoded;
                try
                {
                    decoded = Convert.FromBase64String(part.Trim());
                }
                catch (FormatException)
                {
                    return false;
                }

                if (decoded.Length == 0)
                {
                    continue;
                }

                switch (decoded[0] & 0x1F)
                {
                    case 7:
                        sps = decoded;
                        break;
                    case 8:
                        pps = decoded;
                        break;
                }
            }
        }

        return true;
    }

    /// <summary>从 <c>config=1408</c>（hex）取 AAC AudioSpecificConfig。</summary>
    private static bool TryParseAudioConfig(string fmtp, out byte[]? audioSpecificConfig)
    {
        audioSpecificConfig = null;

        foreach (var item in fmtp.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = item.IndexOf('=');
            if (equals <= 0 || !item[..equals].Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = item[(equals + 1)..].Trim().Trim('"');
            if (value.Length == 0 || value.Length % 2 != 0)
            {
                return false;
            }

            try
            {
                audioSpecificConfig = Convert.FromHexString(value);
                return audioSpecificConfig.Length > 0;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return false;
    }
}
