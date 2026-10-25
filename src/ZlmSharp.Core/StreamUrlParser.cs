namespace ZlmSharp.Core;

/// <summary>解析后的流地址：<c>[vhost/]app/stream.schema.format</c>。</summary>
public readonly record struct StreamUrl(string Vhost, string App, string Stream, string Schema, string Format)
{
    public StreamKey Key => StreamKey.Create(Vhost, App, Stream);

    public override string ToString() => $"{Vhost}/{App}/{Stream}.{Schema}.{Format}";
}

/// <summary>
/// 流 URL 解析（HTTP-FLV/WS-FLV/RTSP/RTMP 共用）：
/// <c>/live/test.live.flv</c>、<c>/vhost/live/test.live.flv</c>、<c>http://host:port/live/test.live.flv?x=1</c>。
/// </summary>
public static class StreamUrlParser
{
    public static bool TryParse(string? input, out StreamUrl url, out string error)
    {
        url = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "路径为空";
            return false;
        }

        var path = input.Trim();

        if (path.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                error = "非法 URL";
                return false;
            }

            path = uri.AbsolutePath;
        }

        var queryIndex = path.IndexOfAny(['?', '#']);
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string vhost;
        string app;
        string streamPart;

        if (segments.Length == 2)
        {
            vhost = StreamKey.DefaultVhost;
            app = segments[0];
            streamPart = segments[1];
        }
        else if (segments.Length == 3)
        {
            vhost = segments[0];
            app = segments[1];
            streamPart = segments[2];
        }
        else
        {
            error = $"路径段数必须是 2 或 3（app/stream.schema.format 或 vhost/app/stream.schema.format），实际: {path}";
            return false;
        }

        var parts = streamPart.Split('.');
        if (parts.Length < 3)
        {
            error = $"流名需形如 stream.schema.format，实际: {streamPart}";
            return false;
        }

        var format = parts[^1];
        var schema = parts[^2];
        var stream = string.Join('.', parts[..^2]);

        if (stream.Length == 0 || schema.Length == 0 || format.Length == 0 || vhost.Length == 0 || app.Length == 0)
        {
            error = $"流地址不完整: {path}";
            return false;
        }

        url = new StreamUrl(vhost, app, stream, schema.ToLowerInvariant(), format.ToLowerInvariant());
        return true;
    }
}
