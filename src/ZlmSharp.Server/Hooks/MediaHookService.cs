using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;
using ZlmSharp.Core;

namespace ZlmSharp.Server.Hooks;

/// <summary>
/// 媒体事件 hook 的 HTTP 客户端实现（对应 ZLM <c>[hook]</c>）。
/// POST JSON 给外部服务，响应 <c>{"code":0}</c> 放行，非 0 拒绝。
/// 也用于 <c>on_stream_none_reader</c>：code 0 表示允许关闭无人观看的流。
/// </summary>
public sealed class MediaHookService
{
    private readonly AppOptions _options;
    private readonly ILogger _logger;
    private readonly HttpClient _client;

    public MediaHookService(AppOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger("ZlmSharp.Hook");
        _client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // 超时由每次调用自己的 CancellationTokenSource 控制，避免并发调用互相影响
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public bool Enabled => _options.Hook.Enable;

    /// <summary>推流/播放前的鉴权（可作为 <see cref="MediaHookHandler"/> 注入协议库）。</summary>
    public async Task<MediaHookResult> InvokeAsync(MediaHookContext context, CancellationToken cancellationToken)
    {
        var hook = _options.Hook;
        if (!hook.Enable)
        {
            return MediaHookResult.Ok;
        }

        var isPublish = context.Type == MediaHookType.Publish;
        var url = isPublish ? hook.OnPublish : hook.OnPlay;
        if (string.IsNullOrWhiteSpace(url))
        {
            return MediaHookResult.Ok;
        }

        var payload = NewPayload(context.Schema, context.Key, context.Id);
        payload.Ip = context.Ip;
        payload.Port = context.Port;
        payload.UserAgent = context.UserAgent;
        payload.Params = BuildParams(context.Params);
        payload.Type = isPublish ? "publish" : "play";

        var name = isPublish ? "on_publish" : "on_play";
        var (ok, code, message) = await PostAsync(url, payload, name, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            _logger.LogWarning(
                "{Hook} 拒绝 {Key}（code={Code}, {Message}）",
                name,
                context.Key,
                code,
                message);
            return MediaHookResult.Deny(message ?? $"{name} 拒绝", code);
        }

        _logger.LogDebug("{Hook} 放行 {Key}", name, context.Key);
        return MediaHookResult.Ok;
    }

    /// <summary>
    /// 无人观看时询问外部是否允许关流。未配置/未启用返回 true（允许关）。
    /// 调用失败时返回 false（保守：宁可不关）。
    /// </summary>
    public async Task<bool> ShouldCloseNoneReaderAsync(
        StreamKey key,
        string schema,
        string? id,
        CancellationToken cancellationToken)
    {
        var hook = _options.Hook;
        if (!hook.Enable || string.IsNullOrWhiteSpace(hook.OnStreamNoneReader))
        {
            return true;
        }

        var payload = NewPayload(schema, key, id);
        payload.Type = "none_reader";

        var (ok, code, message) = await PostAsync(
                hook.OnStreamNoneReader,
                payload,
                "on_stream_none_reader",
                cancellationToken)
            .ConfigureAwait(false);

        if (!ok)
        {
            _logger.LogWarning(
                "on_stream_none_reader 调用失败，保留流 {Key}（{Message}）",
                key,
                message);
            return false;
        }

        return code == 0;
    }

    private static HookPayload NewPayload(string schema, StreamKey key, string? id) => new()
    {
        App = key.App,
        Stream = key.Stream,
        Vhost = key.Vhost,
        Schema = schema,
        Id = id,
    };

    private async Task<(bool Ok, int Code, string? Message)> PostAsync(
        string url,
        HookPayload payload,
        string hookName,
        CancellationToken cancellationToken)
    {
        payload.MediaServerId = _options.General.MediaServerId;

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, HookJsonContext.Default.HookPayload);
        var retry = Math.Max(0, _options.Hook.Retry);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.Hook.TimeoutSec));
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, _options.Hook.RetryDelayMS));

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);

                using var content = new ByteArrayContent(json);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using var response = await _client
                    .PostAsync(url, content, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var text = await SafeReadAsync(response, timeoutCts.Token).ConfigureAwait(false);
                    return (false, (int)response.StatusCode, $"HTTP {(int)response.StatusCode} {text}".Trim());
                }

                await using var body = await response.Content
                    .ReadAsStreamAsync(timeoutCts.Token)
                    .ConfigureAwait(false);

                using var document = await JsonDocument
                    .ParseAsync(body, cancellationToken: timeoutCts.Token)
                    .ConfigureAwait(false);

                var code = -1;
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("code", out var codeElement) &&
                    codeElement.TryGetInt32(out var parsed))
                {
                    code = parsed;
                }

                var message = document.RootElement.ValueKind == JsonValueKind.Object &&
                              document.RootElement.TryGetProperty("msg", out var msgElement)
                    ? msgElement.GetString()
                    : null;

                return (code == 0, code, message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < retry)
            {
                _logger.LogWarning(
                    "{Hook} 第 {Attempt} 次调用失败（{Message}），{Delay}ms 后重试",
                    hookName,
                    attempt + 1,
                    exception.Message,
                    delay.TotalMilliseconds);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
            catch (Exception exception)
            {
                var message = $"hook 调用失败: {exception.Message}";
                _logger.LogError(exception, "{Hook} 调用失败 {Url}", hookName, url);

                // 鉴权 hook 失败时默认拒绝（fail-closed），可通过 hook.fail_open=1 改为放行
                return _options.Hook.FailOpen ? (true, 0, message) : (false, -1, message);
            }
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return text.Length <= 200 ? text : text[..200];
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string BuildParams(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var pair in parameters)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(pair.Key)).Append('=').Append(Uri.EscapeDataString(pair.Value));
        }

        return builder.ToString();
    }
}

/// <summary>hook 请求体。字段名与 ZLM 的 hook 参数保持一致（外部服务大多按此解析）。</summary>
internal sealed class HookPayload
{
    [JsonPropertyName("mediaServerId")]
    public string? MediaServerId { get; set; }

    [JsonPropertyName("app")]
    public string? App { get; set; }

    [JsonPropertyName("stream")]
    public string? Stream { get; set; }

    [JsonPropertyName("vhost")]
    public string? Vhost { get; set; }

    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("user_agent")]
    public string? UserAgent { get; set; }

    /// <summary>查询参数原样串（<c>token=abc&amp;uid=1</c>）。</summary>
    [JsonPropertyName("params")]
    public string Params { get; set; } = string.Empty;

    /// <summary>zlmsharp 扩展：<c>publish</c> / <c>play</c> / <c>none_reader</c>。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HookPayload))]
internal sealed partial class HookJsonContext : JsonSerializerContext
{
}
