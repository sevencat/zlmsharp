using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ZlmSharp.Server;

/// <summary>
/// 内置管理页面：挂在 <c>/zlm/admin</c> 下，<c>GET /zlm/admin</c>（等价 <c>/zlm/admin/</c>）返回单文件 <c>index.html</c>
/// （前端由 web/ 工程构建：Vue 3 + Buefy + singlefile）。
/// <para>
/// 页面以<b>预压缩资源</b>形式内嵌在程序集里（<c>index.html.br</c> / <c>index.html.gz</c>），
/// 部署时不需要随 exe 附带 WebUI 目录；按 <c>Accept-Encoding</c> 协商选择 brotli 或 gzip，
/// 客户端两者都不支持时回退到解压后的明文（HTTP/1.0、curl、旧内核浏览器也能打开）。
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
        // 授权器可选：宿主没注册时（老版本行为）直接放行
        var authorizer = endpoints.ServiceProvider.GetService<IAdminPageAuthorizer>();

        endpoints.MapGet(RoutePrefix, (RequestDelegate)(context => ServeAsync(context, authorizer)));
        endpoints.MapGet(RoutePrefix + "/{file}", (RequestDelegate)(context =>
            string.Equals(context.Request.RouteValues["file"]?.ToString(), FileName, StringComparison.OrdinalIgnoreCase)
                ? ServeAsync(context, authorizer)
                : NotFoundAsync(context, "not found")));
    }

    private static async Task ServeAsync(HttpContext context, IAdminPageAuthorizer? authorizer)
    {
        if (authorizer is not null &&
            !await authorizer.AuthorizeAsync(context, context.RequestAborted).ConfigureAwait(false))
        {
            // 极简令牌输入页：提交后跳到 ?token=xxx，由上面的授权器校验并写 cookie
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = ContentType;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(
                    "<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">"
                    + "<title>需要访问令牌</title>"
                    + "<form method=\"get\" action=\"/zlm/admin\" style=\"margin:3em auto;max-width:20em;font:16px sans-serif\">"
                    + "<label>访问令牌 <input name=\"token\" type=\"password\" autofocus required "
                    + "style=\"display:block;width:100%;margin:.5em 0 1em;padding:.5em\"></label>"
                    + "<button style=\"padding:.5em 1.5em\">进入</button>"
                    + "</form>",
                    context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        var assembly = typeof(AdminPageEndpoints).Assembly;
        var accept = context.Request.Headers.AcceptEncoding.ToString();

        var (resourceName, encoding) = PickResource(assembly, accept);

        // 客户端不支持 br/gzip（curl、HTTP/1.0、旧内核浏览器）：回退到解压后的明文，
        // 不能直接 406，否则这些客户端完全打不开控制台。
        var etag = resourceName is null
            ? PlainEtag(assembly)
            : $"\"{encoding}-{ResourceLength(assembly, resourceName):x}-{assembly.ManifestModule.ModuleVersionId:N}\"";

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

        if (resourceName is null)
        {
            var plain = PlainHtml.Value;
            if (plain is null)
            {
                await NotFoundAsync(
                        context,
                        "管理页面资源缺失：请先在 web/ 目录执行 npm run build"
                        + "（会把 index.html.br / .gz 写回 src/ZlmSharp/WebUI/）。")
                    .ConfigureAwait(false);
                return;
            }

            await context.Response.Body.WriteAsync(plain, context.RequestAborted).ConfigureAwait(false);
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

        context.Response.Headers.ContentEncoding = encoding;
        await resource.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private static long ResourceLength(System.Reflection.Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        return stream?.Length ?? 0;
    }

    private static string PlainEtag(System.Reflection.Assembly assembly) =>
        $"\"plain-{PlainHtml.Value?.Length ?? 0:x}-{assembly.ManifestModule.ModuleVersionId:N}\"";

    /// <summary>解压后的明文页面（只在客户端不支持 br/gzip 时用到），进程内缓存一次。</summary>
    private static readonly Lazy<byte[]?> PlainHtml = new(DecodeEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    private static byte[]? DecodeEmbedded()
    {
        var assembly = typeof(AdminPageEndpoints).Assembly;

        if (assembly.GetManifestResourceStream(GzipResource) is { } gz)
        {
            using (gz)
            using (var gunzip = new GZipStream(gz, CompressionMode.Decompress))
            using (var buffer = new MemoryStream())
            {
                gunzip.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        if (assembly.GetManifestResourceStream(BrotliResource) is { } br)
        {
            using (br)
            using (var unbrotli = new BrotliStream(br, CompressionMode.Decompress))
            using (var buffer = new MemoryStream())
            {
                unbrotli.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        return null;
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
