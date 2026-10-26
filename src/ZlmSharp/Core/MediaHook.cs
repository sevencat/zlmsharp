namespace ZlmSharp.Core;

/// <summary>触发 hook 的事件类型。</summary>
public enum MediaHookType
{
    /// <summary>推流开始前（对应 ZLM <c>on_publish</c>）。</summary>
    Publish,

    /// <summary>播放开始前（对应 ZLM <c>on_play</c>）。</summary>
    Play,
}

/// <summary>
/// 一次媒体事件的上下文：协议库负责填充，宿主负责发 HTTP 请求判定。
/// <see cref="Params"/> 来自推/拉地址的查询参数（例如 <c>?token=xxx</c>），是「按流鉴权」的载体。
/// </summary>
public sealed record MediaHookContext(
    string Schema,
    MediaHookType Type,
    StreamKey Key,
    string Id,
    string? Ip,
    int Port,
    IReadOnlyDictionary<string, string> Params,
    string? UserAgent);

/// <summary>hook 判定结果：<see cref="Allow"/> 为 false 时按拒绝处理。</summary>
public readonly record struct MediaHookResult(bool Allow, int Code, string? Message)
{
    public static MediaHookResult Ok { get; } = new(true, 0, "success");

    public static MediaHookResult Deny(string message, int code = -1) => new(false, code, message);
}

/// <summary>
/// 媒体事件钩子（协议库不依赖 HttpClient/日志框架，由宿主注入实现）。
/// 返回 <see cref="MediaHookResult.Allow"/>=false 表示拒绝该推流/播放。
/// </summary>
public delegate Task<MediaHookResult> MediaHookHandler(MediaHookContext context, CancellationToken cancellationToken);

/// <summary>解析 URL 查询参数（<c>a=1&amp;b=2</c>），空值/无参数返回空字典。</summary>
public static class MediaHookParams
{
    public static IReadOnlyDictionary<string, string> Parse(string? uriOrStreamName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(uriOrStreamName))
        {
            return result;
        }

        var index = uriOrStreamName.IndexOf('?');
        if (index < 0 || index == uriOrStreamName.Length - 1)
        {
            return result;
        }

        var query = uriOrStreamName[(index + 1)..];
        var hash = query.IndexOf('#');
        if (hash >= 0)
        {
            query = query[..hash];
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (name.Length > 0)
            {
                result[name] = value;
            }
        }

        return result;
    }

    /// <summary>去掉 URL 或流名里的查询串（<c>live/test?token=1</c> → <c>live/test</c>）。</summary>
    public static string StripQuery(string value)
    {
        var index = value.IndexOfAny(['?', '#']);
        return index < 0 ? value : value[..index];
    }
}
