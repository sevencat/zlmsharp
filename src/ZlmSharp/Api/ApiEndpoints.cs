using System.Globalization;
using System.Net;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZlmSharp.Ai;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Codecs;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Recording;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Api;

/// <summary>
/// <c>/index/api/*</c> 端点：version / getApiList / getMediaList / close_streams / getSessionList / kick_session /
/// yoloInfo / yoloDetect / getYoloResult / startYoloOverlay / stopYoloOverlay / getYoloOverlay /
/// startRecord / stopRecord / isRecording / startPush / stopPush / getPushList / addStreamProxy / delStreamProxy。
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
        "/index/api/getSessionList",
        "/index/api/kick_session",
        "/index/api/getMp4RecordFile",
        "/index/api/deleteRecordFile",
        "/index/api/yoloInfo",
        "/index/api/yoloDetect",
        "/index/api/getYoloResult",
        "/index/api/analyzeResult",
        "/index/api/startYoloOverlay",
        "/index/api/stopYoloOverlay",
        "/index/api/getYoloOverlay",
        "/index/api/startRecord",
        "/index/api/stopRecord",
        "/index/api/isRecording",
        "/index/api/startPush",
        "/index/api/stopPush",
        "/index/api/getPushList",
        "/index/api/addStreamProxy",
        "/index/api/delStreamProxy",
    ];

    public static void Map(IEndpointRouteBuilder endpoints, AppOptions options, ApiVersionInfo version)
    {
        var registry = endpoints.ServiceProvider.GetRequiredService<MediaSourceRegistry>();
        var sessions = endpoints.ServiceProvider.GetService<MediaSessionRegistry>();
        var detector = endpoints.ServiceProvider.GetService<YoloDetectorService>();
        var yoloStore = endpoints.ServiceProvider.GetService<YoloResultStore>();
        var analyses = endpoints.ServiceProvider.GetService<AnalysisResultStore>();
        var overlays = endpoints.ServiceProvider.GetService<YoloOverlayManager>();
        var recorder = endpoints.ServiceProvider.GetService<RecorderManager>();
        var recordFiles = endpoints.ServiceProvider.GetService<RecordFileService>();
        var pushes = endpoints.ServiceProvider.GetService<RtmpPushManager>();
        var proxies = endpoints.ServiceProvider.GetService<RtmpPullProxyManager>();

        endpoints.MapGet("/index/api/version", (RequestDelegate)(context => VersionAsync(context, options, version)));
        endpoints.MapGet("/index/api/getApiList", (RequestDelegate)(context => ApiListAsync(context, options)));
        endpoints.MapGet("/index/api/getMediaList", (RequestDelegate)(context => MediaListAsync(context, options, registry)));
        endpoints.MapGet("/index/api/close_streams", (RequestDelegate)(context => CloseStreamsAsync(context, options, registry)));
        endpoints.MapGet("/index/api/getSessionList", (RequestDelegate)(context => SessionListAsync(context, options, sessions)));
        endpoints.MapGet("/index/api/kick_session", (RequestDelegate)(context => KickSessionAsync(context, options, sessions)));
        endpoints.MapPost("/index/api/kick_session", (RequestDelegate)(context => KickSessionAsync(context, options, sessions)));
        endpoints.MapGet("/index/api/yoloInfo", (RequestDelegate)(context => YoloInfoAsync(context, options, detector)));
        endpoints.MapPost("/index/api/yoloDetect", (RequestDelegate)(context => YoloDetectAsync(context, options, detector)));
        endpoints.MapGet("/index/api/getYoloResult", (RequestDelegate)(context => YoloResultAsync(context, options, yoloStore)));
        endpoints.MapGet("/index/api/analyzeResult", (RequestDelegate)(context => AnalyzeResultAsync(context, options, analyses)));
        endpoints.MapGet("/index/api/startYoloOverlay", (RequestDelegate)(context => StartYoloOverlayAsync(context, options, detector, overlays)));
        endpoints.MapGet("/index/api/stopYoloOverlay", (RequestDelegate)(context => StopYoloOverlayAsync(context, options, overlays)));
        endpoints.MapGet("/index/api/getYoloOverlay", (RequestDelegate)(context => GetYoloOverlayAsync(context, options, overlays)));
        endpoints.MapGet("/index/api/startRecord", (RequestDelegate)(context => StartRecordAsync(context, options, recorder)));
        endpoints.MapGet("/index/api/stopRecord", (RequestDelegate)(context => StopRecordAsync(context, options, recorder)));
        endpoints.MapGet("/index/api/isRecording", (RequestDelegate)(context => IsRecordingAsync(context, options, recorder)));
        endpoints.MapGet("/index/api/getMp4RecordFile", (RequestDelegate)(context => GetRecordFileAsync(context, options, recordFiles)));
        endpoints.MapGet("/index/api/deleteRecordFile", (RequestDelegate)(context => DeleteRecordFileAsync(context, options, recordFiles)));
        endpoints.MapGet("/index/api/startPush", (RequestDelegate)(context => StartPushAsync(context, options, pushes)));
        endpoints.MapGet("/index/api/stopPush", (RequestDelegate)(context => StopPushAsync(context, options, pushes)));
        endpoints.MapGet("/index/api/getPushList", (RequestDelegate)(context => GetPushListAsync(context, options, pushes)));
        endpoints.MapGet("/index/api/addStreamProxy", (RequestDelegate)(context => AddStreamProxyAsync(context, options, proxies)));
        endpoints.MapGet("/index/api/delStreamProxy", (RequestDelegate)(context => DelStreamProxyAsync(context, options, proxies)));
    }

    /// <summary>拉流代理：把远端 rtmp 拉到本地 <c>vhost/app/stream</c>（也可用返回的 key 停止）。</summary>
    private static async Task AddStreamProxyAsync(HttpContext context, AppOptions options, RtmpPullProxyManager? proxies)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "addStreamProxy");

        if (proxies is null)
        {
            await WriteJsonAsync(context, new ApiErrorResponse(-1, "拉流代理未启用"), ApiJsonContext.Default.ApiErrorResponse);
            return;
        }

        try
        {
            var key = proxies.Start(
                context.Request.Query["vhost"].ToString(),
                context.Request.Query["app"].ToString(),
                context.Request.Query["stream"].ToString(),
                context.Request.Query["url"].ToString());

            await WriteJsonAsync(
                context,
                new ApiProxyKeyResponse(0, new ApiProxyKeyData(key.ToString())),
                ApiJsonContext.Default.ApiProxyKeyResponse);
        }
        catch (ArgumentException exception)
        {
            await WriteJsonAsync(context, new ApiErrorResponse(-1, exception.Message), ApiJsonContext.Default.ApiErrorResponse);
        }
    }

    private static async Task DelStreamProxyAsync(HttpContext context, AppOptions options, RtmpPullProxyManager? proxies)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "delStreamProxy");

        if (proxies is null)
        {
            await WriteJsonAsync(context, new ApiErrorResponse(-1, "拉流代理未启用"), ApiJsonContext.Default.ApiErrorResponse);
            return;
        }

        var keyText = context.Request.Query["key"].ToString();
        StreamKey key;

        if (keyText.Length > 0)
        {
            var parts = keyText.Split('/');
            if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
            {
                await WriteJsonAsync(
                    context,
                    new ApiErrorResponse(-1, $"key 格式应为 vhost/app/stream：{keyText}"),
                    ApiJsonContext.Default.ApiErrorResponse);
                return;
            }

            key = StreamKey.Create(parts[0], parts[1], parts[2]);
        }
        else
        {
            var app = context.Request.Query["app"].ToString();
            var stream = context.Request.Query["stream"].ToString();
            if (app.Length == 0 || stream.Length == 0)
            {
                await WriteJsonAsync(
                    context,
                    new ApiErrorResponse(-1, "缺少 key 或 app/stream 参数"),
                    ApiJsonContext.Default.ApiErrorResponse);
                return;
            }

            key = StreamKey.Create(context.Request.Query["vhost"].ToString(), app, stream);
        }

        var flag = proxies.Stop(key);
        await WriteJsonAsync(context, new ApiProxyFlagResponse(0, new ApiProxyFlagData(flag)), ApiJsonContext.Default.ApiProxyFlagResponse);
    }

    /// <summary>查询定时检测结果（每路流只保留最近一次）。</summary>
    private static async Task YoloResultAsync(HttpContext context, AppOptions options, YoloResultStore? store)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "getYoloResult");

        if (store is null)
        {
            await WriteJsonAsync(context, new ApiYoloResultResponse(0, []), ApiJsonContext.Default.ApiYoloResultResponse);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var snapshots = store.List(
            context.Request.Query["vhost"].ToString(),
            context.Request.Query["app"].ToString(),
            context.Request.Query["stream"].ToString());

        var data = new List<ApiYoloSnapshot>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var objects = new List<ApiYoloObject>(snapshot.Objects.Count);
            foreach (var item in snapshot.Objects)
            {
                objects.Add(new ApiYoloObject(
                    item.ClassId,
                    item.ClassName,
                    item.Confidence,
                    item.X,
                    item.Y,
                    item.Width,
                    item.Height,
                    item.TrackId));
            }

            data.Add(new ApiYoloSnapshot(
                snapshot.Vhost,
                snapshot.App,
                snapshot.Stream,
                snapshot.At.ToUnixTimeSeconds(),
                (long)Math.Max(0, (now - snapshot.At).TotalMilliseconds),
                snapshot.ElapsedMs,
                snapshot.Width,
                snapshot.Height,
                objects.Count,
                objects,
                snapshot.Error));
        }

        await WriteJsonAsync(context, new ApiYoloResultResponse(0, data), ApiJsonContext.Default.ApiYoloResultResponse);
    }

    /// <summary>通用分析结果：任意分析器（yolo / ocr / …）的最新快照，可用 <c>analyzer=</c> 过滤。</summary>
    private static async Task AnalyzeResultAsync(HttpContext context, AppOptions options, AnalysisResultStore? store)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "analyzeResult");

        var query = context.Request.Query;
        IReadOnlyList<AnalysisResult> results = store is null
            ? []
            : store.List(
                query["vhost"].ToString(),
                query["app"].ToString(),
                query["stream"].ToString(),
                query["analyzer"].ToString());

        var now = DateTimeOffset.UtcNow;
        var data = new List<ApiAnalysisData>(results.Count);
        foreach (var result in results)
        {
            var primitives = new List<ApiAnalysisPrimitive>(result.Primitives.Count);
            foreach (var item in result.Primitives)
            {
                primitives.Add(new ApiAnalysisPrimitive(
                    item.Kind.ToString().ToLowerInvariant(),
                    item.Label,
                    item.Score,
                    item.TrackId,
                    item.X,
                    item.Y,
                    item.Width,
                    item.Height,
                    item.Points?.Select(point => new ApiAnalysisPoint(point.X, point.Y)).ToArray(),
                    item.Text));
            }

            data.Add(new ApiAnalysisData(
                result.AnalyzerId,
                result.Key.Vhost,
                result.Key.App,
                result.Key.Stream,
                result.At.ToUnixTimeSeconds(),
                (long)Math.Max(0, (now - result.At).TotalMilliseconds),
                result.ElapsedMs,
                result.Width,
                result.Height,
                primitives.Count,
                primitives,
                result.Error));
        }

        await WriteJsonAsync(
            context,
            new ApiAnalyzeResultResponse(0, null, data),
            ApiJsonContext.Default.ApiAnalyzeResultResponse);
    }

    /// <summary>
    /// 开始结果叠加：ffmpeg 解码源流 → 画框 → 编码推回本机 RTMP，输出 <c>{stream}_yolo</c>。
    /// 可选 <c>fps</c>（默认源帧率或 25）、<c>bitrate</c>（kbps，默认 1500）、<c>scale</c>（标签缩放，默认 2）。
    /// </summary>
    private static async Task StartYoloOverlayAsync(
        HttpContext context,
        AppOptions options,
        YoloDetectorService? detector,
        YoloOverlayManager? overlays)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "startYoloOverlay");

        if (overlays is null || detector is null || !detector.Enabled)
        {
            await WriteJsonAsync(
                context,
                new ApiYoloOverlayResponse(-1, "yolo 未启用：请在 .env 配置 ZLM_YOLO_MODEL", null),
                ApiJsonContext.Default.ApiYoloOverlayResponse);
            return;
        }

        var app = context.Request.Query["app"].ToString();
        var stream = context.Request.Query["stream"].ToString();
        if (app.Length == 0 || stream.Length == 0)
        {
            await WriteJsonAsync(
                context,
                new ApiYoloOverlayResponse(-1, "请提供 app 与 stream 参数", null),
                ApiJsonContext.Default.ApiYoloOverlayResponse);
            return;
        }

        var query = context.Request.Query;
        int? fps = int.TryParse(query["fps"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFps) && parsedFps > 0
            ? parsedFps
            : null;
        var bitrate = int.TryParse(query["bitrate"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBitrate)
            ? parsedBitrate
            : 0;
        var scale = int.TryParse(query["scale"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedScale)
            ? parsedScale
            : 2;

        try
        {
            var status = overlays.Start(query["vhost"].ToString(), app, stream, fps, bitrate, scale);
            await WriteJsonAsync(
                context,
                new ApiYoloOverlayResponse(0, null, ToApiOverlay(status)),
                ApiJsonContext.Default.ApiYoloOverlayResponse);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            await WriteJsonAsync(
                context,
                new ApiYoloOverlayResponse(-1, exception.Message, null),
                ApiJsonContext.Default.ApiYoloOverlayResponse);
        }
    }

    private static async Task StopYoloOverlayAsync(HttpContext context, AppOptions options, YoloOverlayManager? overlays)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "stopYoloOverlay");

        if (overlays is null)
        {
            await WriteJsonAsync(
                context,
                new ApiYoloOverlayFlagResponse(0, new ApiYoloOverlayFlagData(false)),
                ApiJsonContext.Default.ApiYoloOverlayFlagResponse);
            return;
        }

        var flag = overlays.Stop(
            context.Request.Query["vhost"].ToString(),
            context.Request.Query["app"].ToString(),
            context.Request.Query["stream"].ToString());

        await WriteJsonAsync(
            context,
            new ApiYoloOverlayFlagResponse(0, new ApiYoloOverlayFlagData(flag)),
            ApiJsonContext.Default.ApiYoloOverlayFlagResponse);
    }

    private static async Task GetYoloOverlayAsync(HttpContext context, AppOptions options, YoloOverlayManager? overlays)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "getYoloOverlay");

        ApiYoloOverlayData[] data = overlays is null
            ? []
            : overlays.List(
                    context.Request.Query["vhost"].ToString(),
                    context.Request.Query["app"].ToString(),
                    context.Request.Query["stream"].ToString())
                .Select(ToApiOverlay)
                .ToArray();

        await WriteJsonAsync(
            context,
            new ApiYoloOverlayListResponse(0, data),
            ApiJsonContext.Default.ApiYoloOverlayListResponse);
    }

    private static ApiYoloOverlayData ToApiOverlay(YoloOverlayStatus status) => new(
        status.Vhost,
        status.App,
        status.Stream,
        status.OverlayStream,
        status.InputUrl,
        status.OutputUrl,
        status.State.ToString().ToLowerInvariant(),
        status.Width,
        status.Height,
        status.Fps,
        status.BitrateKbps,
        status.Frames,
        status.StartedAt.ToUnixTimeSeconds(),
        (long)Math.Max(0, status.ElapsedMs),
        status.Error);

    /// <summary>
    /// 开始录像。<c>type</c>：1=MP4（默认）、2=FLV、3=两者；<c>max_second</c> 切片秒数（默认取 <c>protocol.mp4_max_second</c>）。
    /// </summary>
    private static async Task StartRecordAsync(HttpContext context, AppOptions options, RecorderManager? recorder)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "startRecord");

        if (recorder is null)
        {
            await WriteJsonAsync(
                context,
                new ApiRecordResponse(-1, "录像未启用：请注册 AddZlmSharpRecording", null),
                ApiJsonContext.Default.ApiRecordResponse);
            return;
        }

        var query = context.Request.Query;
        var app = query["app"].ToString();
        var stream = query["stream"].ToString();
        if (app.Length == 0 || stream.Length == 0)
        {
            await WriteJsonAsync(
                context,
                new ApiRecordResponse(-1, "请提供 app 与 stream 参数", null),
                ApiJsonContext.Default.ApiRecordResponse);
            return;
        }

        var type = int.TryParse(query["type"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedType)
            ? parsedType
            : 1;
        int? maxSeconds = int.TryParse(query["max_second"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeconds)
            ? parsedSeconds
            : null;

        try
        {
            var status = recorder.Start(
                StreamKey.Create(query["vhost"].ToString(), app, stream),
                RecordFormats.FromType(type),
                maxSeconds);

            await WriteJsonAsync(
                context,
                new ApiRecordResponse(0, null, ToApiRecord(status)),
                ApiJsonContext.Default.ApiRecordResponse);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            await WriteJsonAsync(
                context,
                new ApiRecordResponse(-1, exception.Message, null),
                ApiJsonContext.Default.ApiRecordResponse);
        }
    }

    private static async Task StopRecordAsync(HttpContext context, AppOptions options, RecorderManager? recorder)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "stopRecord");

        var flag = recorder?.Stop(
            context.Request.Query["vhost"].ToString(),
            context.Request.Query["app"].ToString(),
            context.Request.Query["stream"].ToString()) == true;

        await WriteJsonAsync(
            context,
            new ApiRecordFlagResponse(0, new ApiRecordFlagData(flag)),
            ApiJsonContext.Default.ApiRecordFlagResponse);
    }

    private static async Task IsRecordingAsync(HttpContext context, AppOptions options, RecorderManager? recorder)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "isRecording");

        var status = recorder?.Get(
            context.Request.Query["vhost"].ToString(),
            context.Request.Query["app"].ToString(),
            context.Request.Query["stream"].ToString());

        await WriteJsonAsync(
            context,
            new ApiRecordResponse(0, null, status is null ? null : ToApiRecord(status)),
            ApiJsonContext.Default.ApiRecordResponse);
    }

    private static ApiRecordData ToApiRecord(RecordingStatus status) => new(
        status.Vhost,
        status.App,
        status.Stream,
        status.State.ToString().ToLowerInvariant(),
        status.Formats.HasFlag(RecordFormat.Mp4),
        status.Formats.HasFlag(RecordFormat.Flv),
        status.Mp4Path,
        status.FlvPath,
        status.Frames,
        status.DurationMs,
        status.StartedAt.ToUnixTimeSeconds(),
        status.Error);

    /// <summary>RTMP 转推：把本地 <c>app/stream</c> 推到 <c>url=rtmp://host/app/stream</c>。</summary>
    private static async Task StartPushAsync(HttpContext context, AppOptions options, RtmpPushManager? pushes)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "startPush");

        if (pushes is null)
        {
            await WriteJsonAsync(
                context,
                new ApiPushResponse(-1, "转推未启用", null),
                ApiJsonContext.Default.ApiPushResponse);
            return;
        }

        var query = context.Request.Query;
        var app = query["app"].ToString();
        var stream = query["stream"].ToString();
        var target = query["url"].ToString();
        if (app.Length == 0 || stream.Length == 0 || target.Length == 0)
        {
            await WriteJsonAsync(
                context,
                new ApiPushResponse(-1, "请提供 app、stream 与 url 参数", null),
                ApiJsonContext.Default.ApiPushResponse);
            return;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var url))
        {
            await WriteJsonAsync(
                context,
                new ApiPushResponse(-1, $"url 不是合法地址：{target}", null),
                ApiJsonContext.Default.ApiPushResponse);
            return;
        }

        try
        {
            var status = pushes.Start(query["vhost"].ToString(), app, stream, url);
            await WriteJsonAsync(
                context,
                new ApiPushResponse(0, null, ToApiPush(status)),
                ApiJsonContext.Default.ApiPushResponse);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            await WriteJsonAsync(
                context,
                new ApiPushResponse(-1, exception.Message, null),
                ApiJsonContext.Default.ApiPushResponse);
        }
    }

    private static async Task StopPushAsync(HttpContext context, AppOptions options, RtmpPushManager? pushes)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "stopPush");

        var flag = pushes?.Stop(
            context.Request.Query["vhost"].ToString(),
            context.Request.Query["app"].ToString(),
            context.Request.Query["stream"].ToString()) == true;

        await WriteJsonAsync(
            context,
            new ApiPushFlagResponse(0, new ApiPushFlagData(flag)),
            ApiJsonContext.Default.ApiPushFlagResponse);
    }

    private static async Task GetPushListAsync(HttpContext context, AppOptions options, RtmpPushManager? pushes)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "getPushList");

        var query = context.Request.Query;
        ApiPushData[] data = pushes is null
            ? []
            : pushes.List(query["vhost"].ToString(), query["app"].ToString(), query["stream"].ToString())
                .Select(ToApiPush)
                .ToArray();

        await WriteJsonAsync(
            context,
            new ApiPushListResponse(0, data),
            ApiJsonContext.Default.ApiPushListResponse);
    }

    private static ApiPushData ToApiPush(PushStatus status) => new(
        status.Vhost,
        status.App,
        status.Stream,
        status.Url,
        status.State,
        status.Frames,
        status.StartedAt.ToUnixTimeSeconds(),
        Math.Max(0, status.ElapsedMs),
        status.Error);

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
                    item.Height,
                    item.TrackId));
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

    /// <summary>录像文件浏览：<c>period</c> 为空返回日期目录列表，否则返回该日期下的文件。</summary>
    private static async Task GetRecordFileAsync(
        HttpContext context,
        AppOptions options,
        RecordFileService? files)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "getMp4RecordFile");

        if (files is null)
        {
            await WriteJsonAsync(
                context,
                new ApiErrorResponse(-1, "录像未启用"),
                ApiJsonContext.Default.ApiErrorResponse);
            return;
        }

        var app = context.Request.Query["app"].ToString();
        var stream = context.Request.Query["stream"].ToString();
        var period = context.Request.Query["period"].ToString();
        var path = context.Request.Query["path"].ToString();

        var paths = files.List(app, stream, period, path.Length == 0 ? null : path);

        await WriteJsonAsync(
            context,
            new ApiRecordFileResponse(0, new ApiRecordFileData(files.Root, paths)),
            ApiJsonContext.Default.ApiRecordFileResponse);
    }

    /// <summary>删除录像文件：<c>path</c>（相对录像根目录）或 <c>app/stream/period/file_name</c>。</summary>
    private static async Task DeleteRecordFileAsync(
        HttpContext context,
        AppOptions options,
        RecordFileService? files)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "deleteRecordFile");

        if (files is null)
        {
            await WriteJsonAsync(
                context,
                new ApiErrorResponse(-1, "录像未启用"),
                ApiJsonContext.Default.ApiErrorResponse);
            return;
        }

        var path = context.Request.Query["path"].ToString();
        var app = context.Request.Query["app"].ToString();
        var stream = context.Request.Query["stream"].ToString();
        var period = context.Request.Query["period"].ToString();
        var fileName = context.Request.Query["file_name"].ToString();

        var deleted = new List<string>();
        if (path.Length > 0)
        {
            if (files.Delete(path))
            {
                deleted.Add(path);
            }
        }
        else if (fileName.Length > 0 && app.Length > 0 && stream.Length > 0)
        {
            if (files.Delete(app, stream, period, fileName))
            {
                deleted.Add($"{app}/{stream}/{period}/{fileName}");
            }
        }
        else
        {
            await WriteJsonAsync(
                context,
                new ApiErrorResponse(-1, "缺少 path 或 app/stream/file_name 参数"),
                ApiJsonContext.Default.ApiErrorResponse);
            return;
        }

        await WriteJsonAsync(
            context,
            new ApiDeleteRecordFileResponse(0, new ApiDeleteRecordFileData(deleted.Count, deleted)),
            ApiJsonContext.Default.ApiDeleteRecordFileResponse);
    }

    private static async Task SessionListAsync(
        HttpContext context,
        AppOptions options,
        MediaSessionRegistry? sessions)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "getSessionList");

        if (sessions is null)
        {
            await WriteJsonAsync(
                context,
                new ApiSessionListResponse(0, []),
                ApiJsonContext.Default.ApiSessionListResponse);
            return;
        }

        var vhost = context.Request.Query["vhost"].ToString();
        var app = context.Request.Query["app"].ToString();
        var stream = context.Request.Query["stream"].ToString();
        var schema = context.Request.Query["schema"].ToString();
        var type = context.Request.Query["type"].ToString();
        var now = DateTime.UtcNow;

        var data = new List<ApiSessionInfo>();
        foreach (var session in sessions.Snapshot())
        {
            if (!MatchesFilter(session.Key.Vhost, vhost) ||
                !MatchesFilter(session.Key.App, app) ||
                !MatchesFilter(session.Key.Stream, stream) ||
                !MatchesFilter(session.Schema, schema) ||
                !MatchesType(session.Type, type))
            {
                continue;
            }

            data.Add(new ApiSessionInfo(
                session.Id,
                session.Schema,
                SessionTypeName(session.Type),
                session.Key.Vhost,
                session.Key.App,
                session.Key.Stream,
                session.Ip ?? string.Empty,
                session.Port,
                (long)Math.Max(0, (now - session.CreatedAt).TotalSeconds),
                session.UserAgent));
        }

        await WriteJsonAsync(
            context,
            new ApiSessionListResponse(0, data),
            ApiJsonContext.Default.ApiSessionListResponse);
    }

    /// <summary>踢掉推流/播放会话：<c>id=</c> 精确匹配，或按 vhost/app/stream/type 批量。</summary>
    private static async Task KickSessionAsync(
        HttpContext context,
        AppOptions options,
        MediaSessionRegistry? sessions)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "kick_session");

        if (sessions is null)
        {
            await WriteJsonAsync(
                context,
                new ApiErrorResponse(-1, "会话注册表未启用"),
                ApiJsonContext.Default.ApiErrorResponse);
            return;
        }

        var id = context.Request.Query["id"].ToString();
        var vhost = context.Request.Query["vhost"].ToString();
        var app = context.Request.Query["app"].ToString();
        var stream = context.Request.Query["stream"].ToString();
        var schema = context.Request.Query["schema"].ToString();
        var type = context.Request.Query["type"].ToString();

        IReadOnlyList<MediaSessionInfo> kicked;
        if (id.Length > 0)
        {
            kicked = sessions.Kick(info => string.Equals(info.Id, id, StringComparison.Ordinal));
        }
        else
        {
            if (vhost.Length == 0 && app.Length == 0 && stream.Length == 0 && schema.Length == 0 && type.Length == 0)
            {
                await WriteJsonAsync(
                    context,
                    new ApiErrorResponse(-1, "需要 id 或至少一个过滤条件"),
                    ApiJsonContext.Default.ApiErrorResponse);
                return;
            }

            kicked = sessions.Kick(info =>
                MatchesFilter(info.Key.Vhost, vhost) &&
                MatchesFilter(info.Key.App, app) &&
                MatchesFilter(info.Key.Stream, stream) &&
                MatchesFilter(info.Schema, schema) &&
                MatchesType(info.Type, type));
        }

        var ids = new string[kicked.Count];
        for (var index = 0; index < kicked.Count; index++)
        {
            ids[index] = kicked[index].Id;
        }

        await WriteJsonAsync(
            context,
            new ApiKickSessionResponse(0, new ApiKickSessionData(kicked.Count, ids)),
            ApiJsonContext.Default.ApiKickSessionResponse);
    }

    private static bool MatchesType(MediaSessionType type, string filter) => filter.ToLowerInvariant() switch
    {
        "" => true,
        "publish" or "publisher" => type == MediaSessionType.Publisher,
        "play" or "player" => type == MediaSessionType.Player,
        _ => false,
    };

    private static string SessionTypeName(MediaSessionType type) =>
        type == MediaSessionType.Publisher ? "publish" : "play";

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
            VideoCodecConfigResolver.Resolve(source); // 回填宽高/编码信息，便于 API 展示
        }

        return BuildMediaInfo(source);
    }

    private static ApiMediaInfo BuildMediaInfo(MediaSource source) => new(
        source.Key.Vhost,
        source.Key.App,
        source.Key.Stream,
        source.Schema,
        source.OriginType,
        (long)Math.Max(0, (DateTime.UtcNow - source.CreatedAt).TotalSeconds),
        source.ReaderCount,
        source.TotalReaderCount,
        CodecName(source.Video.CodecId),
        source.Video.Width,
        source.Video.Height,
        source.Video.Fps,
        CodecName(source.Audio.CodecId),
        source.Audio.SampleRate,
        source.Audio.Channels,
        source.TotalDroppedFrames);

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
