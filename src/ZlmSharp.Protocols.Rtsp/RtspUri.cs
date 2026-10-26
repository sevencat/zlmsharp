namespace ZlmSharp.Protocols.Rtsp;

/// <summary>RTSP 目标 URI 解析：<c>rtsp://host:port/[vhost/]app/stream[/trackID=0|streamid=0]</c>。</summary>
public static class RtspUri
{
    public static bool TryParse(string uri, out string app, out string stream, out string? vhost)
    {
        app = string.Empty;
        stream = string.Empty;
        vhost = null;

        var path = uri;
        if (uri.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return false;
            }

            path = parsed.AbsolutePath;
        }

        var queryIndex = path.IndexOfAny(['?', '#']);
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && IsTrackSegment(segments[^1]))
        {
            segments = segments[..^1];
        }

        if (segments.Length == 2)
        {
            app = segments[0];
            stream = segments[1];
        }
        else if (segments.Length == 3)
        {
            vhost = segments[0];
            app = segments[1];
            stream = segments[2];
        }
        else
        {
            return false;
        }

        return app.Length > 0 && stream.Length > 0;
    }

    /// <summary>去掉末尾的 <c>/trackID=0</c> / <c>/streamid=0</c> 等轨道段。</summary>
    public static string BuildBaseUri(string uri)
    {
        var trimmed = uri.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash > 0 && IsTrackSegment(trimmed[(slash + 1)..]) ? trimmed[..slash] : trimmed;
    }

    /// <summary>
    /// 轨道段：<c>trackID=0</c> / <c>streamid=0</c> / <c>track1</c>。
    /// 不把普通的 stream 名误判成轨道（例如 <c>live/cam</c>）。
    /// </summary>
    private static bool IsTrackSegment(string segment) =>
        segment.StartsWith("trackID", StringComparison.OrdinalIgnoreCase) ||
        segment.StartsWith("streamid", StringComparison.OrdinalIgnoreCase);
}
