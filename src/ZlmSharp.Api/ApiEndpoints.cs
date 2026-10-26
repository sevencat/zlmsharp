using System.Globalization;
using System.Net;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZlmSharp.Ai;
using ZlmSharp.Codecs;
using ZlmSharp.Configuration;
using ZlmSharp.Core;

namespace ZlmSharp.Api;

/// <summary>
/// <c>/index/api/*</c> 端点：version / getApiList / getMediaList / close_streams / yoloInfo / yoloDetect。
/// 返回体与 ZLM 保持一致（<c>code</c>/<c>msg</c>/<c>data</c> 字段）。
/// </summary>
public static class ApiEndpoints
{
    private const int MaxYoloImageBytes = 32 * 1024 * 1024;

    private static readonly string[] ImplementedApis =
    [
        "/index/api/version",
        "/index/api/getApiList",
        "/index/api/getMediaList",
        "/index/api/close_streams",
        "/index/api/yoloInfo",
        "/index/api/yoloDetect",
    ];

    public static void Map(IEndpointRouteBuilder endpoints, AppOptions options, ApiVersionInfo version)
    {
        var registry = endpoints.ServiceProvider.GetRequiredService<MediaSourceRegistry>();
        var detector = endpoints.ServiceProvider.GetService<YoloDetectorService>();

        endpoints.MapGet("/index/api/version", (RequestDelegate)(context => VersionAsync(context, options, version)));
        endpoints.MapGet("/index/api/getApiList", (RequestDelegate)(context => ApiListAsync(context, options)));
        endpoints.MapGet("/index/api/getMediaList", (RequestDelegate)(context => MediaListAsync(context, options, registry)));
        endpoints.MapGet("/index/api/close_streams", (RequestDelegate)(context => CloseStreamsAsync(context, options, registry)));
        endpoints.MapGet("/index/api/yoloInfo", (RequestDelegate)(context => YoloInfoAsync(context, options, detector)));
        endpoints.MapPost("/index/api/yoloDetect", (RequestDelegate)(context => YoloDetectAsync(context, options, detector)));
    }

    private static async Task YoloInfoAsync(HttpContext context, AppOptions options, YoloDetectorService? detector)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "yoloInfo");

        if (detector is null || (!detector.Enabled && detector.ModelDir.Length == 0))
        {
            await WriteJsonAsync(
                context,
                new ApiYoloInfoResponse(-1, "yolo 未启用：请在 .env 配置 ZLM_YOLO_MODEL / ZLM_YOLO_MODEL_DIR", null),
                ApiJsonContext.Default.ApiYoloInfoResponse);
            return;
        }

        YoloModelSummary? summary = null;
        if (detector.Enabled)
        {
            summary = detector.ReadModelSummary();
            if (summary is null)
            {
                await WriteJsonAsync(
                    context,
                    new ApiYoloInfoResponse(-1, $"无法读取 YOLO 模型（路径或格式不受支持）：{detector.ModelPath}", null),
                    ApiJsonContext.Default.ApiYoloInfoResponse);
                return;
            }
        }

        var data = new ApiYoloInfoData(
            detector.Enabled,
            detector.Loaded,
            detector.ModelPath,
            detector.ModelDir,
            detector.Device,
            detector.Options.InputWidth,
            detector.Options.InputHeight,
            detector.Options.Confidence,
            summary?.ClassCount ?? 0,
            summary?.Task,
            summary?.ClassNames ?? [],
            detector.AvailableModels,
            detector.LoadedModels);

        await WriteJsonAsync(context, new ApiYoloInfoResponse(0, null, data), ApiJsonContext.Default.ApiYoloInfoResponse);
    }

    /// <summary>
    /// 检测一张图：body 为 P6 PPM 图片，或用 <c>url</c> 参数指定流地址（ffmpeg 抓一帧）；
    /// <c>confidence</c> 可覆盖默认阈值。
    /// </summary>
    private static async Task YoloDetectAsync(HttpContext context, AppOptions options, YoloDetectorService? detector)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "yoloDetect");

        if (detector is null || (!detector.Enabled && detector.ModelDir.Length == 0))
        {
            await WriteJsonAsync(
                context,
                new ApiYoloDetectResponse(-1, "yolo 未启用：请在 .env 配置 ZLM_YOLO_MODEL / ZLM_YOLO_MODEL_DIR", null),
                ApiJsonContext.Default.ApiYoloDetectResponse);
            return;
        }

        var cancellationToken = context.RequestAborted;
        var url = context.Request.Query["url"].ToString();
        var model = context.Request.Query["model"].ToString();

        // 先校验模型名，避免白跑一次 ffmpeg 抽帧
        try
        {
            detector.ResolveModelPath(model);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FileNotFoundException)
        {
            await WriteJsonAsync(
                context,
                new ApiYoloDetectResponse(-1, exception.Message, null),
                ApiJsonContext.Default.ApiYoloDetectResponse);
            return;
        }

        RgbImage image;

        try
        {
            if (url.Length > 0)
            {
                image = await FfmpegFrameGrabber.GrabAsync(
                    options.Env.ResolveFfmpegPath(),
                    url,
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
            }
            else
            {
                var body = await ReadBodyAsync(context, MaxYoloImageBytes, cancellationToken);
                if (body is null)
                {
                    await WriteJsonAsync(
                        context,
                        new ApiYoloDetectResponse(-1, $"图片超过上限 {MaxYoloImageBytes / 1024 / 1024}MB", null),
                        ApiJsonContext.Default.ApiYoloDetectResponse);
                    return;
                }

                if (body.Length == 0)
                {
                    await WriteJsonAsync(
                        context,
                        new ApiYoloDetectResponse(-1, "请提供 P6 PPM 图片 body，或用 url 参数指定流地址", null),
                        ApiJsonContext.Default.ApiYoloDetectResponse);
                    return;
                }

                if (!PpmImage.TryParse(body, out var parsed, out var parseError))
                {
                    await WriteJsonAsync(
                        context,
                        new ApiYoloDetectResponse(-1, parseError, null),
                        ApiJsonContext.Default.ApiYoloDetectResponse);
                    return;
                }

                image = parsed;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GetLogger(context)?.LogWarning(exception, "YOLO 取帧失败: {Url}", url);
            await WriteJsonAsync(
                context,
                new ApiYoloDetectResponse(-1, exception.Message, null),
                ApiJsonContext.Default.ApiYoloDetectResponse);
            return;
        }

        float? confidence = null;
        if (float.TryParse(
                context.Request.Query["confidence"].ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedConfidence))
        {
            confidence = parsedConfidence;
        }

        try
        {
            var result = await detector.DetectAsync(image, confidence, model, cancellationToken);
            var objects = new List<ApiYoloObject>(result.Objects.Count);
            foreach (var item in result.Objects)
            {
                objects.Add(new ApiYoloObject(
                    item.ClassId,
                    item.ClassName,
                    item.Confidence,
                    item.X,
                    item.Y,
                    item.Width,
                    item.Height));
            }

            var data = new ApiYoloDetectData(result.Width, result.Height, result.ElapsedMs, objects.Count, objects);
            await WriteJsonAsync(
                context,
                new ApiYoloDetectResponse(0, null, data),
                ApiJsonContext.Default.ApiYoloDetectResponse);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GetLogger(context)?.LogWarning(exception, "YOLO 检测失败");
            await WriteJsonAsync(
                context,
                new ApiYoloDetectResponse(-1, exception.Message, null),
                ApiJsonContext.Default.ApiYoloDetectResponse);
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];

        while (true)
        {
            var read = await context.Request.Body.ReadAsync(chunk, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task VersionAsync(HttpContext context, AppOptions options, ApiVersionInfo version)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "version");
        var response = new ApiVersionResponse(
            Code: 0,
            Data: new ApiVersionData(version.BuildTime, version.BranchName, version.CommitHash));
        await WriteJsonAsync(context, response, ApiJsonContext.Default.ApiVersionResponse);
    }

    private static async Task ApiListAsync(HttpContext context, AppOptions options)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "getApiList");
        var response = new ApiListResponse(0, ImplementedApis);
        await WriteJsonAsync(context, response, ApiJsonContext.Default.ApiListResponse);
    }

    private static async Task MediaListAsync(HttpContext context, AppOptions options, MediaSourceRegistry registry)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "getMediaList");

        var vhost = context.Request.Query["vhost"].ToString();
        var app = context.Request.Query["app"].ToString();
        var stream = context.Request.Query["stream"].ToString();
        var schema = context.Request.Query["schema"].ToString();

        var data = new List<ApiMediaInfo>();
        foreach (var source in registry.Sources)
        {
            if (Matches(source, vhost, app, stream, schema))
            {
                data.Add(ToMediaInfo(source));
            }
        }

        await WriteJsonAsync(context, new ApiMediaListResponse(0, data), ApiJsonContext.Default.ApiMediaListResponse);
    }

    private static async Task CloseStreamsAsync(HttpContext context, AppOptions options, MediaSourceRegistry registry)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "close_streams");

        var vhost = context.Request.Query["vhost"].ToString();
        var app = context.Request.Query["app"].ToString();
        var stream = context.Request.Query["stream"].ToString();
        var schema = context.Request.Query["schema"].ToString();

        var count = 0;
        foreach (var source in registry.Sources.ToArray())
        {
            if (!Matches(source, vhost, app, stream, schema))
            {
                continue;
            }

            count++;
            registry.Remove(source.Key, out _);
        }

        await WriteJsonAsync(
            context,
            new ApiCloseStreamsResponse(0, count, count > 0),
            ApiJsonContext.Default.ApiCloseStreamsResponse);
    }

    private static bool Matches(MediaSource source, string vhost, string app, string stream, string schema) =>
        MatchesFilter(source.Key.Vhost, vhost) &&
        MatchesFilter(source.Key.App, app) &&
        MatchesFilter(source.Key.Stream, stream) &&
        MatchesFilter(source.Schema, schema);

    private static bool MatchesFilter(string value, string filter) =>
        filter.Length == 0 || string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);

    private static ApiMediaInfo ToMediaInfo(MediaSource source)
    {
        if (source.Video.Width <= 0)
        {
            H264ConfigResolver.Resolve(source); // 回填宽高/编码信息，便于 API 展示
        }

        return BuildMediaInfo(source);
    }

    private static ApiMediaInfo BuildMediaInfo(MediaSource source) => new(
        source.Key.Vhost,
        source.Key.App,
        source.Key.Stream,
        source.Schema,
        source.Schema,
        (long)Math.Max(0, (DateTime.UtcNow - source.CreatedAt).TotalSeconds),
        source.ReaderCount,
        source.TotalReaderCount,
        CodecName(source.Video.CodecId),
        source.Video.Width,
        source.Video.Height,
        source.Video.Fps,
        CodecName(source.Audio.CodecId),
        source.Audio.SampleRate,
        source.Audio.Channels);

    private static string CodecName(CodecId codecId) => codecId switch
    {
        CodecId.H264 => "H264",
        CodecId.H265 => "H265",
        CodecId.Aac => "AAC",
        CodecId.Opus => "Opus",
        CodecId.G711A => "G711A",
        CodecId.G711U => "G711U",
        _ => "unknown",
    };

    /// <summary>secret 校验：本机回环免校验（与 ZLM 行为一致），失败返回 401 + 错误体。</summary>
    private static async Task<bool> AuthorizeAsync(HttpContext context, AppOptions options)
    {
        var remote = Normalize(context.Connection.RemoteIpAddress);
        if (remote is not null && IPAddress.IsLoopback(remote))
        {
            return true;
        }

        if (string.IsNullOrEmpty(options.Api.Secret))
        {
            return true;
        }

        var provided = context.Request.Query["secret"].ToString();
        if (string.Equals(provided, options.Api.Secret, StringComparison.Ordinal))
        {
            return true;
        }

        var logger = GetLogger(context);
        logger?.LogWarning("API 鉴权失败: {RemoteIp} {Path}", remote, context.Request.Path);
        await WriteJsonAsync(context, new ApiErrorResponse(-1, "invalid secret"), ApiJsonContext.Default.ApiErrorResponse, StatusCodes.Status401Unauthorized);
        return false;
    }

    private static IPAddress? Normalize(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    private static void LogRequest(HttpContext context, AppOptions options, string api)
    {
        if (!options.Api.Debug)
        {
            return;
        }

        var logger = GetLogger(context);
        logger?.LogInformation(
            "API {Api} 来自 {RemoteIp}，查询参数: {Query}",
            api,
            context.Connection.RemoteIpAddress,
            context.Request.QueryString.Value);
    }

    private static ILogger? GetLogger(HttpContext context) =>
        context.RequestServices.GetService(typeof(ILoggerFactory)) is ILoggerFactory factory
            ? factory.CreateLogger("ZlmSharp.Api")
            : null;

    private static Task WriteJsonAsync<T>(HttpContext context, T value, JsonTypeInfo<T> typeInfo, int statusCode = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(value, typeInfo, cancellationToken: context.RequestAborted);
    }
}
