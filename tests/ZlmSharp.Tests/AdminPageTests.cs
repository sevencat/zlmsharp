using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>
/// 内置管理页面：单文件 html 内嵌在程序集里（gz 预压缩），
/// 支持 Accept-Encoding 协商、明文回退与 ETag，且不遮蔽 API / HTTP-FLV 路由。
/// </summary>
public class AdminPageTests
{
    /// <summary>管理页面路由前缀（与 AdminPageEndpoints.RoutePrefix 对齐）。</summary>
    private const string PagePath = "/zlm/admin/";

    [Fact]
    public async Task Root_ReturnsConsolePage()
    {
        var (app, address) = await StartHostAsync();
        await using var _ = app;

        using var client = CreateClient();
        using var response = await client.GetAsync($"{address}{PagePath}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        // 不带 Accept-Encoding 时回退到明文（不能 406）
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Contains("<html", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrecompressedVariants_AreNegotiated()
    {
        var (app, address) = await StartHostAsync();
        await using var _ = app;

        using var client = CreateClient();

        // 只保留 gzip：客户端即使同时接受 br，也只会拿到 gzip
        var (bothStatus, bothEncoding, bothLength) = await GetAsync(client, $"{address}{PagePath}", "br, gzip");
        Assert.Equal(HttpStatusCode.OK, bothStatus);
        Assert.Equal("gzip", bothEncoding);
        Assert.True(bothLength > 0);

        var (gzStatus, gzEncoding, gzLength) = await GetAsync(client, $"{address}{PagePath}", "gzip");
        Assert.Equal(HttpStatusCode.OK, gzStatus);
        Assert.Equal("gzip", gzEncoding);
        Assert.True(gzLength > 0);

        var (rawStatus, rawEncoding, rawLength) = await GetAsync(client, $"{address}{PagePath}", null);
        Assert.Equal(HttpStatusCode.OK, rawStatus);
        Assert.Null(rawEncoding);
        Assert.True(rawLength > 0);

        using var raw = await client.GetAsync($"{address}{PagePath}");
        var rawBody = await raw.Content.ReadAsStringAsync();
        Assert.True(rawBody.Contains("<html", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Etag_Returns304OnSecondRequest_AndDiffersByEncoding()
    {
        var (app, address) = await StartHostAsync();
        await using var _ = app;

        using var client = CreateClient();
        using var first = await client.GetAsync($"{address}{PagePath}");
        var etag = first.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrEmpty(etag));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{address}{PagePath}");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var second = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);

        // 压缩版与明文版内容不同，ETag 必须不同，否则缓存会串
        using var gzipRequest = new HttpRequestMessage(HttpMethod.Get, $"{address}{PagePath}");
        gzipRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        using var gzipResponse = await client.SendAsync(gzipRequest);
        Assert.Equal("gzip", gzipResponse.Content.Headers.ContentEncoding.FirstOrDefault());
        Assert.NotEqual(etag, gzipResponse.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task AdminAliases_ServeSamePage_AndUnknownFileIs404()
    {
        var (app, address) = await StartHostAsync();
        await using var _ = app;

        using var client = CreateClient();

        // 带尾部斜杠（/zlm/admin/）、不带斜杠（/zlm/admin）与显式 index.html 都应命中
        foreach (var path in new[] { "/zlm/admin", "/zlm/admin/", "/zlm/admin/index.html" })
        {
            using var response = await client.GetAsync($"{address}{path}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("<html", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        using var missing = await client.GetAsync($"{address}/zlm/admin/app.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task AdminPage_DoesNotShadowApiOrFlvRoutes()
    {
        var (app, address) = await StartHostAsync();
        await using var _ = app;

        using var client = CreateClient();

        var version = await client.GetAsync($"{address}/index/api/version");
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Contains("\"code\":0", await version.Content.ReadAsStringAsync());

        // 管理页面挂在 /zlm/admin：根路径与旧的 /admin 都不占用
        foreach (var path in new[] { "/", "/admin" })
        {
            using var legacy = await client.GetAsync($"{address}{path}");
            Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
        }

        // 未推流的 FLV 路径仍走 HTTP-FLV 端点（404 + 文本），不会被管理页面兜底成 HTML
        using var flv = await client.GetAsync($"{address}/live/missing.live.flv");
        Assert.Equal(HttpStatusCode.NotFound, flv.StatusCode);
        Assert.DoesNotContain("<html", await flv.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PageToken_RequiresToken_AndRemembersCookie()
    {
        var (app, address) = await StartHostAsync(options => options.Api.PageToken = "s3cret");
        await using var _ = app;

        using var anonymous = CreateClient();
        using var denied = await anonymous.GetAsync($"{address}{PagePath}");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var wrong = await anonymous.GetAsync($"{address}{PagePath}?token=nope");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // 查询串 token 正确 → 放行并写入 cookie
        using var ok = await anonymous.GetAsync($"{address}{PagePath}?token=s3cret");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("zlm_admin_token", ok.Headers.GetValues("Set-Cookie").Single());

        // 同一客户端后续不带 token 也能打开（cookie 生效，覆盖 /zlm/admin 与 /zlm/admin/）
        using var cached = await anonymous.GetAsync($"{address}{PagePath}");
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);

        // 请求头也可以（用全新客户端，避免 cookie 干扰）
        using var viaHeader = CreateClient();
        using var headerRequest = new HttpRequestMessage(HttpMethod.Get, $"{address}{PagePath}");
        headerRequest.Headers.TryAddWithoutValidation("X-ZlmSharp-Token", "s3cret");
        using var headerResponse = await viaHeader.SendAsync(headerRequest);
        Assert.Equal(HttpStatusCode.OK, headerResponse.StatusCode);
    }

    [Fact]
    public async Task CustomAuthorizer_OverridesDefaultToken()
    {
        var (app, address) = await StartHostAsync(
            options => options.Api.PageToken = "ignored",
            services => services.AddSingleton<IAdminPageAuthorizer, HeaderOnlyAuthorizer>());
        await using var _ = app;

        using var client = CreateClient();

        // 默认的 pageToken 已被宿主实现接管，不再生效
        using var byToken = await client.GetAsync($"{address}{PagePath}?token=ignored");
        Assert.Equal(HttpStatusCode.Unauthorized, byToken.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{address}{PagePath}");
        request.Headers.TryAddWithoutValidation("X-Test-Allow", "1");
        using var allowed = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <summary>宿主自定义授权器：只看自己的请求头。</summary>
    private sealed class HeaderOnlyAuthorizer : IAdminPageAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(HttpContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(context.Request.Headers.ContainsKey("X-Test-Allow"));
    }

    private static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static async Task<(HttpStatusCode Status, string? Encoding, int Length)> GetAsync(
        HttpClient client,
        string url,
        string? acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (acceptEncoding is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return (response.StatusCode, encoding, bytes.Length);
    }

    private static async Task<(WebApplication App, string Address)> StartHostAsync(
        Action<AppOptions>? configure = null,
        Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var options = new AppOptions();
        options.Http.AllowOrigins = "*";
        options.General.MaxStreamWaitMS = 500;
        options.General.WaitTrackReadyMS = 500;
        options.Rtsp.Port = 0;
        options.Rtmp.Port = 0;
        configure?.Invoke(options);

        builder.Services.AddZlmSharp(o =>
        {
            o.Options = options;
            o.ConfigureLogging = false;
            o.RegisterStreamListeners = false;
        });

        // 宿主自定义注册放在 AddZlmSharp 之后，可覆盖默认实现
        services?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseZlmSharp();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses
            .First()
            .Replace("[::]", "127.0.0.1")
            .TrimEnd('/');

        return (app, address);
    }
}
