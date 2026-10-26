using System.Globalization;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>ANNOUNCE SDP 里的 H.264 视频轨道信息。</summary>
public sealed record SdpH264Track(
    int PayloadType,
    int ClockRate,
    string Control,
    byte[]? Sps,
    byte[]? Pps)
{
    public bool HasParameterSets => Sps is { Length: > 0 } && Pps is { Length: > 0 };
}

/// <summary>只解析「H.264 视频推流」需要的最小 SDP 子集（m=video / rtpmap / fmtp / control）。</summary>
public static class SdpParser
{
    public static bool TryParseH264(string? sdp, out SdpH264Track? track, out string error)
    {
        track = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sdp))
        {
            error = "SDP 为空";
            return false;
        }

        string? sessionControl = null;
        string? mediaControl = null;
        string? fmtp = null;
        string? encoding = null;
        int? payloadType = null;
        var clockRate = 0;
        var inVideo = false;

        foreach (var raw in sdp.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                inVideo = line.StartsWith("m=video ", StringComparison.OrdinalIgnoreCase);
                if (inVideo)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    payloadType = parts.Length > 0 && int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : null;
                    encoding = null;
                    clockRate = 0;
                    fmtp = null;
                    mediaControl = null;
                }

                continue;
            }

            if (!inVideo)
            {
                if (sessionControl is null && line.StartsWith("a=control:", StringComparison.OrdinalIgnoreCase))
                {
                    sessionControl = line["a=control:".Length..].Trim();
                }

                continue;
            }

            if (line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase))
            {
                ParseRtpMap(line, ref payloadType, ref encoding, ref clockRate);
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

        if (payloadType is null || !string.Equals(encoding, "H264", StringComparison.OrdinalIgnoreCase))
        {
            error = "SDP 中没有 H.264 视频轨道";
            return false;
        }

        byte[]? sps = null;
        byte[]? pps = null;
        if (!string.IsNullOrEmpty(fmtp) && !TryParseParameterSets(fmtp, out sps, out pps, out var formatError))
        {
            error = formatError;
            return false;
        }

        track = new SdpH264Track(
            payloadType.Value,
            clockRate > 0 ? clockRate : 90000,
            mediaControl ?? sessionControl ?? string.Empty,
            sps,
            pps);

        return true;
    }

    private static void ParseRtpMap(string line, ref int? payloadType, ref string? encoding, ref int clockRate)
    {
        var (pt, value) = ParseAttributeWithPayload(line, "a=rtpmap:");
        if (pt is null || value is null || (payloadType is { } expected && pt != expected))
        {
            return;
        }

        var slash = value.IndexOf('/');
        var name = slash > 0 ? value[..slash] : value;
        if (slash > 0 && int.TryParse(value[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate))
        {
            clockRate = rate;
        }

        encoding = name.Trim();
        payloadType ??= pt;
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

    private static bool TryParseParameterSets(
        string fmtp,
        out byte[]? sps,
        out byte[]? pps,
        out string error)
    {
        sps = null;
        pps = null;
        error = string.Empty;

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
                    error = "sprop-parameter-sets 不是合法 base64";
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
}
