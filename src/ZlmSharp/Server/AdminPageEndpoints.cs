using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ZlmSharp.Server;

/// <summary>
/// 内置管理页面：挂在 <c>/zlm/admin</c> 下，<c>GET /zlm/admin</c>（等价 <c>/zlm/admin/</c>）返回单文件 <c>index.html</c>
/// （前端由 web/ 工程构建：Vue 3 + Buefy + singlefile）。
/// <para>
/// 页面以<b>预压缩资源</b>形式内嵌在程序集里（<c>index.html.br</c> / <c>index.html.gz</c>），
/// 部署时不需要随 exe 附带 WebUI 目录；按 <c>Accept-Encoding</c> 协商选择 brotli 或 gzip，
/// 两者都不支持时返回 406（现代浏览器均支持，未压缩原文不再提供，省掉一半体积）。
/// </para>
/// 支持 <c>ETag</c> + <c>If-None-Match</c>，重复打开页面只回 304。
/// 不占用 <c>/</c>（留给宿主），也不会与 <c>/index/api/*</c>、HTTP-FLV 的 catch-all 冲突。
/// </summary>
internal static class AdminPageEndpoints
{
    /// <summary>页面路由前缀；路由层对尾部斜杠宽容，<c>/zlm/admin</c> 与 <c>/zlm/admin/</c> 都能命中。</summary>
    private const string RoutePrefix = "/zlm/admin";

    private const string FileName = "index.html";

    /// <summary>内嵌资源名（与 ZlmSharp.csproj 的 LogicalName 一致）。</summary>
    private const string BrotliResource = "ZlmSharp.WebUI.index.html.br";

    private const string GzipResource = "ZlmSharp.WebUI.index.html.gz";

    private const string ContentType = "text/html; charset=utf-8";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(RoutePrefix, (RequestDelegate)ServeAsync);
        endpoints.MapGet(RoutePrefix + "/{file}", (RequestDelegate)(context =>
            string.Equals(context.Request.RouteValues["file"]?.ToString(), FileName, StringComparison.OrdinalIgnoreCase)
                ? ServeAsync(context)
                : NotFoundAsync(context, "not found")));
    }

    private static async Task ServeAsync(HttpContext context)
    {
        var assembly = typeof(AdminPageEndpoints).Assembly;
        var accept = context.Request.Headers.AcceptEncoding.ToString();

        var (resourceName, encoding) = PickResource(assembly, accept);
        if (resourceName is null)
        {
            context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                    "管理页面只提供 brotli / gzip 预压缩版本，请使用支持 Accept-Encoding: br 或 gzip 的浏览器访问。")
                .ConfigureAwait(false);
            return;
        }

        using var resource = assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
        {
            await NotFoundAsync(
                    context,
                    $"管理页面资源缺失：{resourceName}{Environment.NewLine}"
                    + "请先在 web/ 目录执行 npm run build（会把 index.html.br / .gz 写回 src/ZlmSharp/WebUI/）。")
                .ConfigureAwait(false);
            return;
        }

        // 嵌入资源没有文件时间戳：用程序集 MVID（每次构建都会变）参与 ETag
        var etag = $"\"{encoding}-{resource.Length:x}-{assembly.ManifestModule.ModuleVersionId:N}\"";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ETag = etag;

        if (IsNotModified(context, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ContentType;
        context.Response.Headers.Vary = "Accept-Encoding";
        context.Response.Headers.ContentEncoding = encoding;

        await resource.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>挑选内嵌资源：优先 br，其次 gzip；都不可用返回 (null, null)。</summary>
    private static (string? Resource, string? Encoding) PickResource(
        System.Reflection.Assembly assembly,
        string accept)
    {
        if (Accepts(accept, "br") && assembly.GetManifestResourceInfo(BrotliResource) is not null)
        {
            return (BrotliResource, "br");
        }

        if (Accepts(accept, "gzip") && assembly.GetManifestResourceInfo(GzipResource) is not null)
        {
            return (GzipResource, "gzip");
        }

        return (null, null);
    }

    /// <summary>极简 Accept-Encoding 解析：只判断某个编码是否被接受（<c>q=0</c> 视为拒绝，<c>*</c> 视为接受非 br 的编码）。</summary>
    private static bool Accepts(string header, string coding)
    {
        var wildcard = false;

        foreach (var item in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf(';');
            var name = separator < 0 ? item : item[..separator].Trim();
            var rejected = separator >= 0 && item[separator..].Contains("q=0", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(name, coding, StringComparison.OrdinalIgnoreCase))
            {
                return !rejected;
            }

            if (name == "*" && !rejected)
            {
                wildcard = true;
            }
        }

        return wildcard;
    }

    private static bool IsNotModified(HttpContext context, string etag)
    {
        var header = context.Request.Headers.IfNoneMatch.ToString();
        if (header.Length == 0)
        {
            return false;
        }

        foreach (var item in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (item == "*" || string.Equals(item, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Task NotFoundAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message);
    }
}
