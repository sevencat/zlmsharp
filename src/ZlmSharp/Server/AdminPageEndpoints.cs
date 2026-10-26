using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ZlmSharp.Configuration;

namespace ZlmSharp.Server;

/// <summary>
/// 内置管理页面：<c>GET /</c> 返回 index.html，<c>GET /admin/app.js</c> / <c>/admin/style.css</c> 返回静态资源。
/// 优先读 <c>http.rootPath</c> 下的同名文件（便于热更新），缺失时回落到程序集内嵌资源。
/// 路由只占用 <c>/</c> 与 <c>/admin/*</c>，不会与 <c>/index/api/*</c>、HTTP-FLV 的 catch-all 冲突。
/// </summary>
internal static class AdminPageEndpoints
{
    private const string ResourcePrefix = "ZlmSharp.WebUI.";
    private static readonly Assembly Assembly = typeof(AdminPageEndpoints).Assembly;

    private static readonly Dictionary<string, (string Resource, string ContentType)> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["index.html"] = (ResourcePrefix + "index.html", "text/html; charset=utf-8"),
        ["app.js"] = (ResourcePrefix + "app.js", "text/javascript; charset=utf-8"),
        ["style.css"] = (ResourcePrefix + "style.css", "text/css; charset=utf-8"),
    };

    public static void Map(IEndpointRouteBuilder endpoints, AppOptions options)
    {
        endpoints.MapGet("/", (RequestDelegate)(context => ServeAsync(context, options, "index.html")));
        endpoints.MapGet(
            "/admin/{file}",
            (RequestDelegate)(context => ServeAsync(context, options, context.Request.RouteValues["file"]?.ToString())));
    }

    private static async Task ServeAsync(HttpContext context, AppOptions options, string? file)
    {
        if (string.IsNullOrEmpty(file) || !Files.TryGetValue(file, out var entry))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("not found").ConfigureAwait(false);
            return;
        }

        var payload = ReadOverride(options.Http.RootPath, file) ?? ReadEmbedded(entry.Resource);
        if (payload is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("管理页面资源缺失").ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = entry.ContentType;
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentLength = payload.Length;
        await context.Response.Body.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
    }

    private static byte[]? ReadOverride(string rootPath, string file)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        try
        {
            var path = Path.GetFullPath(Path.Combine(rootPath, file));
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[]? ReadEmbedded(string resource)
    {
        using var stream = Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
