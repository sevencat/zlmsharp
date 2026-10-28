using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ZlmSharp.Configuration;

namespace ZlmSharp.Server;

/// <summary>
/// 内置管理页面：<c>GET /</c> 返回单文件 <c>index.html</c>（前端由 web/ 工程构建：Vue 3 + Buefy + singlefile）。
/// 页面不强绑程序集，统一从 <c>http.rootPath</c>（默认 <c>./WebUI</c>，相对可执行文件目录解析）读取，
/// 若同目录存在构建期预压缩的 <c>index.html.br</c> / <c>index.html.gz</c>，按 <c>Accept-Encoding</c> 协商返回；
/// 并支持 <c>ETag</c> + <c>If-None-Match</c>，重复打开页面只回 304。
/// 路由只占用 <c>/</c>、<c>/admin</c> 与 <c>/admin/index.html</c>，不会与 <c>/index/api/*</c>、HTTP-FLV 的 catch-all 冲突。
/// </summary>
internal static class AdminPageEndpoints
{
    private const string FileName = "index.html";

    /// <summary>页面所在目录（相对可执行文件目录），对应 <c>http.rootPath</c> 的默认值。</summary>
    private const string DefaultFolder = "WebUI";

    private const string ContentType = "text/html; charset=utf-8";

    public static void Map(IEndpointRouteBuilder endpoints, AppOptions options)
    {
        endpoints.MapGet("/", (RequestDelegate)(context => ServeAsync(context, options)));
        endpoints.MapGet("/admin", (RequestDelegate)(context => ServeAsync(context, options)));
        endpoints.MapGet("/admin/{file}", (RequestDelegate)(context =>
            string.Equals(context.Request.RouteValues["file"]?.ToString(), FileName, StringComparison.OrdinalIgnoreCase)
                ? ServeAsync(context, options)
                : NotFoundAsync(context, "not found")));
    }

    private static async Task ServeAsync(HttpContext context, AppOptions options)
    {
        var root = ResolveRoot(options.Http.RootPath);
        var source = Path.Combine(root, FileName);
        if (!File.Exists(source))
        {
            // 仅兼容旧默认值：老版本 ini 是 rootPath=./www，该目录废弃后回退到程序目录下的 WebUI/
            var fallback = Path.Combine(AppContext.BaseDirectory, DefaultFolder, FileName);
            if (!IsLegacyRoot(root) || !File.Exists(fallback))
            {
                await NotFoundAsync(
                    context,
                    $"管理页面不存在：{source}{Environment.NewLine}"
                    + $"把前端构建产物 index.html 放到该目录，或用 http.rootPath 指向其它目录（默认 ./{DefaultFolder}）。")
                    .ConfigureAwait(false);
                return;
            }

            source = fallback;
        }

        var (path, encoding) = PickVariant(context, source);
        var info = new FileInfo(path);
        var suffix = encoding is null ? string.Empty : "-" + encoding;
        var etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}{suffix}\"";

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
        if (encoding is not null)
        {
            context.Response.Headers.ContentEncoding = encoding;
        }

        // 直接走 sendfile，避免像旧实现那样每次请求都把整页读进内存
        await context.Response.SendFileAsync(path, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>挑选传输用的文件：优先 br，其次 gzip，都没有就用原文。</summary>
    private static (string Path, string? Encoding) PickVariant(HttpContext context, string source)
    {
        var accept = context.Request.Headers.AcceptEncoding.ToString();
        if (accept.Length > 0)
        {
            if (Accepts(accept, "br") && File.Exists(source + ".br"))
            {
                return (source + ".br", "br");
            }

            if (Accepts(accept, "gzip") && File.Exists(source + ".gz"))
            {
                return (source + ".gz", "gzip");
            }
        }

        return (source, null);
    }

    /// <summary>极简 Accept-Encoding 解析：只判断某个编码是否被接受（<c>q=0</c> 视为拒绝）。</summary>
    private static bool Accepts(string header, string coding)
    {
        foreach (var item in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf(';');
            var name = separator < 0 ? item : item[..separator].Trim();
            if (!string.Equals(name, coding, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return separator < 0 || !item[separator..].Contains("q=0", StringComparison.OrdinalIgnoreCase);
        }

        return false;
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

    /// <summary>
    /// 是否仍是旧版本的默认目录（<c>./www</c>）：只有这种配置才允许回退到 <c>WebUI/</c>，
    /// 用户显式指向的其它目录（哪怕是空的）都不做隐式兜底。
    /// </summary>
    private static bool IsLegacyRoot(string root) =>
        string.Equals(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "www")).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析页面根目录：相对路径以可执行文件所在目录为基准（而不是进程 CWD，
    /// 否则作为 Windows 服务 / 计划任务启动时会去 System32 找 ./WebUI）。
    /// </summary>
    private static string ResolveRoot(string rootPath)
    {
        var path = string.IsNullOrWhiteSpace(rootPath) ? "./" + DefaultFolder : rootPath;
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static Task NotFoundAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message);
    }
}
