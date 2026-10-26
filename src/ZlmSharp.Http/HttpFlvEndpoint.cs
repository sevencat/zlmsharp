using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZlmSharp.Codecs;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Flv;

namespace ZlmSharp.Http;

/// <summary>
/// FLV 拉流端点：<c>/vhost/app/stream.live.flv</c>（vhost 可省略）。
/// 普通 HTTP 请求走 HTTP-FLV；带 WebSocket 升级的同一路径走 WS-FLV。
/// 用 catch-all 路由承载，只处理 <c>.live.flv</c> 后缀；其它路径返回 404。
/// </summary>
public static class HttpFlvEndpoint
{
    private const string Suffix = ".live.flv";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/{**path}", (RequestDelegate)HandleAsync);
    }

    private static Task HandleAsync(HttpContext context)
    {
        var registry = context.RequestServices.GetRequiredService<MediaSourceRegistry>();
        var options = context.RequestServices.GetRequiredService<AppOptions>();
        var sessions = context.RequestServices.GetService<MediaSessionRegistry>();
        var hook = context.RequestServices.GetService<MediaHookHandler>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ZlmSharp.HttpFlv");
        return HandleCoreAsync(context, options, registry, sessions, hook, logger);
    }

    private static async Task HandleCoreAsync(
        HttpContext context,
        AppOptions options,
        MediaSourceRegistry registry,
        MediaSessionRegistry? sessions,
        MediaHookHandler? hook,
        ILogger logger)
    {
        if (context.Request.Path.Value is not { } requestPath ||
            !requestPath.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ||
            !StreamUrlParser.TryParse(requestPath, out var url, out _) ||
            !string.Equals(url.Format, "flv", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(url.Schema, "live", StringComparison.OrdinalIgnoreCase))
        {
            await WriteNotFoundAsync(context, "not found");
            return;
        }

        var schema = context.WebSockets.IsWebSocketRequest ? "ws-flv" : "http-flv";
        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        var remotePort = context.Connection.RemotePort;
        var userAgent = context.Request.Headers.UserAgent.ToString();

        // 会话级 token：kick_session 取消它即可断开这条拉流
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var cancellationToken = sessionCts.Token;

        var session = sessions?.Register(
            schema,
            MediaSessionType.Player,
            url.Key,
            remoteIp,
            remotePort,
            userAgent,
            sessionCts.Cancel);

        try
        {
            if (hook is not null)
            {
                var hookContext = new MediaHookContext(
                    schema,
                    MediaHookType.Play,
                    url.Key,
                    session?.Id ?? string.Empty,
                    remoteIp,
                    remotePort,
                    MediaHookParams.Parse(context.Request.QueryString.Value),
                    userAgent);

                var hookResult = await hook(hookContext, cancellationToken).ConfigureAwait(false);
                if (!hookResult.Allow)
                {
                    logger.LogWarning("FLV 拉流被 hook 拒绝 {Url}: {Message}", url, hookResult.Message);
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    await context.Response.WriteAsync("forbidden", cancellationToken);
                    return;
                }
            }

            var source = await WaitForSourceAsync(registry, url.Key, options.General.MaxStreamWaitMS, cancellationToken)
                .ConfigureAwait(false);
            if (source is null)
            {
                logger.LogInformation("FLV 拉流失败：流不存在 {Url}", url);
                await WriteNotFoundAsync(context, $"stream not found: {url.Key}");
                return;
            }

            var config = await H264ConfigResolver
                .WaitAsync(source, options.General.WaitTrackReadyMS, cancellationToken)
                .ConfigureAwait(false);
            if (config is null)
            {
                logger.LogWarning("FLV 拉流失败：{Key} 未就绪（缺少 SPS/PPS）", source.Key);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("stream not ready");
                return;
            }

            var metadata = new FlvMetadata
            {
                Width = source.Video.Width,
                Height = source.Video.Height,
                FrameRate = source.Video.Fps > 0 ? source.Video.Fps : 25,
            };

            if (context.WebSockets.IsWebSocketRequest)
            {
                await HandleWebSocketAsync(context, source, config, metadata, url, logger).ConfigureAwait(false);
                return;
            }

            await HandleHttpAsync(context, options, source, config, metadata, url, logger).ConfigureAwait(false);
        }
        finally
        {
            sessions?.Unregister(session?.Id);
        }
    }

    private static async Task HandleHttpAsync(
        HttpContext context,
        AppOptions options,
        MediaSource source,
        H264Config config,
        FlvMetadata metadata,
        StreamUrl url,
        ILogger logger)
    {
        var cancellationToken = context.RequestAborted;
        PrepareResponse(context, options);
        var body = context.Response.BodyWriter;

        try
        {
            var (frames, bytes) = await FlvLiveStream
                .RunAsync(source, config, metadata, (data, ct) => WriteBodyAsync(body, data, ct), cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation("HTTP-FLV 结束播放 {Url}：{Frames} 帧 / {Bytes} 字节", url, frames, bytes);
        }
        catch (OperationCanceledException)
        {
            // 客户端断开
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "HTTP-FLV 连接中断 {Url}", url);
        }
    }

    private static async Task HandleWebSocketAsync(
        HttpContext context,
        MediaSource source,
        H264Config config,
        FlvMetadata metadata,
        StreamUrl url,
        ILogger logger)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        logger.LogInformation("WS-FLV 开始播放 {Url}（{Remote}）", url, context.Connection.RemoteIpAddress);

        try
        {
            var (frames, bytes) = await FlvLiveStream
                .RunAsync(
                    source,
                    config,
                    metadata,
                    (data, ct) => WriteWebSocketAsync(socket, data, ct),
                    context.RequestAborted)
                .ConfigureAwait(false);

            logger.LogInformation("WS-FLV 结束播放 {Url}：{Frames} 帧 / {Bytes} 字节", url, frames, bytes);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException exception)
        {
            logger.LogDebug(exception, "WS-FLV 连接中断 {Url}", url);
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "WS-FLV 连接中断 {Url}", url);
        }
        finally
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
                {
                    // 客户端已断开，忽略
                }
            }
        }
    }

    private static async ValueTask WriteBodyAsync(
        PipeWriter body,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        await body.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteWebSocketAsync(
        WebSocket socket,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        await socket.SendAsync(data, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
    }

    private static void PrepareResponse(HttpContext context, AppOptions options)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "video/x-flv";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Pragma = "no-cache";

        var allowOrigins = options.Http.AllowOrigins;
        if (string.IsNullOrWhiteSpace(allowOrigins))
        {
            allowOrigins = "*";
        }

        context.Response.Headers["Access-Control-Allow-Origin"] = allowOrigins;
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    private static async Task<MediaSource?> WaitForSourceAsync(
        MediaSourceRegistry registry,
        StreamKey key,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);

        while (true)
        {
            if (registry.TryGet(key, out var source) && source is { IsClosed: false })
            {
                return source;
            }

            if (timeoutMs <= 0 || Environment.TickCount64 >= deadline)
            {
                return null;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteNotFoundAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message, context.RequestAborted);
    }
}
