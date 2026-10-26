using System.Text;
using ZlmSharp.Codecs;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>SDP 生成（单条视频轨道：H.264 packetization-mode=1 或 H.265）。</summary>
public static class SdpBuilder
{
    public static string BuildH264(
        H264Config config,
        string sessionName = "zlmsharp",
        int payloadType = 96,
        string control = "trackID=0")
    {
        var sprop = $"{Convert.ToBase64String(config.Sps)},{Convert.ToBase64String(config.Pps)}";
        var profileLevelId = BuildProfileLevelId(config);
        var frameRate = "25";

        var builder = new StringBuilder(512);
        builder.Append("v=0\r\n")
            .Append("o=- 0 0 IN IP4 0.0.0.0\r\n")
            .Append("s=").Append(sessionName).Append("\r\n")
            .Append("c=IN IP4 0.0.0.0\r\n")
            .Append("t=0 0\r\n")
            .Append("a=control:*\r\n")
            .Append("a=range:npt=0-\r\n")
            .Append("m=video 0 RTP/AVP ").Append(payloadType).Append("\r\n")
            .Append("a=rtpmap:").Append(payloadType).Append(" H264/90000\r\n")
            .Append("a=fmtp:").Append(payloadType)
            .Append(" packetization-mode=1;profile-level-id=").Append(profileLevelId)
            .Append(";sprop-parameter-sets=").Append(sprop).Append("\r\n")
            .Append("a=framerate:").Append(frameRate).Append("\r\n")
            .Append("a=control:").Append(control).Append("\r\n");

        return builder.ToString();
    }

    /// <summary>HEVC 的 SDP：rtpmap H265/90000 + fmtp 里带 sprop-vps/sps/pps（RFC 7798）。</summary>
    public static string BuildH265(
        H265Config config,
        string sessionName = "zlmsharp",
        int payloadType = 96,
        string control = "trackID=0")
    {
        var builder = new StringBuilder(512);
        builder.Append("v=0\r\n")
            .Append("o=- 0 0 IN IP4 0.0.0.0\r\n")
            .Append("s=").Append(sessionName).Append("\r\n")
            .Append("c=IN IP4 0.0.0.0\r\n")
            .Append("t=0 0\r\n")
            .Append("a=control:*\r\n")
            .Append("a=range:npt=0-\r\n")
            .Append("m=video 0 RTP/AVP ").Append(payloadType).Append("\r\n")
            .Append("a=rtpmap:").Append(payloadType).Append(" H265/90000\r\n")
            .Append("a=fmtp:").Append(payloadType)
            .Append(" sprop-vps=").Append(Convert.ToBase64String(config.Vps))
            .Append(";sprop-sps=").Append(Convert.ToBase64String(config.Sps))
            .Append(";sprop-pps=").Append(Convert.ToBase64String(config.Pps)).Append("\r\n")
            .Append("a=framerate:25\r\n")
            .Append("a=control:").Append(control).Append("\r\n");

        return builder.ToString();
    }

    /// <summary>按编码类型选择 SDP 生成方式。</summary>
    public static string Build(
        VideoCodecConfig config,
        string sessionName = "zlmsharp",
        int payloadType = 96,
        string control = "trackID=0") =>
        config.H265 is { } h265
            ? BuildH265(h265, sessionName, payloadType, control)
            : BuildH264(config.H264!, sessionName, payloadType, control);

    /// <summary>profile-level-id：SPS 第 2~4 字节的十六进制。</summary>
    public static string BuildProfileLevelId(H264Config config)
    {
        var sps = config.Sps;
        if (sps.Length < 4)
        {
            return "42001e";
        }

        return $"{sps[1]:x2}{sps[2]:x2}{sps[3]:x2}";
    }
}
