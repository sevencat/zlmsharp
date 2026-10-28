using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>
/// 内置管理页面：单文件 html 从 http.rootPath 读取，支持预压缩协商与 ETag，
/// 且不遮蔽 API / HTTP-FLV 路由。
/// </summary>
public class AdminPageTests : IDisposable
{
    private const string PageHtml = "<!DOCTYPE html><html><body>zlmsharp 控制台 8f3a</body></html>";

    private readonly string _root;

    public AdminPageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zlmsharp-webui-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task Root_ReturnsConsolePageFromRootPath()
    {
        WritePage();
        var (app, address) = await StartHostAsync(_root);
        await using var _ = app;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync($"{address}/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("zlmsharp 控制台 8f3a", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Root_Returns404WithHint_WhenPageMissing()
    {
        var (app, address) = await StartHostAsync(Path.Combine(_root, "missing"));
        await using var _ = app;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync($"{address}/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("管理页面不存在", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PrecompressedVariants_AreNegotiated()
    {
        WritePage(br: "br-body", gzip: "gz-body");
        var (app, address) = await StartHostAsync(_root);
        await using var _ = app;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var (brStatus, brEncoding, brBody) = await GetAsync(client, $"{address}/", "br, gzip");
        Assert.Equal(HttpStatusCode.OK, brStatus);
        Assert.Equal("br", brEncoding);
        Assert.Equal("br-body", brBody);

        var (gzStatus, gzEncoding, gzBody) = await GetAsync(client, $"{address}/", "gzip");
        Assert.Equal(HttpStatusCode.OK, gzStatus);
        Assert.Equal("gzip", gzEncoding);
        Assert.Equal("gz-body", gzBody);

        var (rawStatus, rawEncoding, rawBody) = await GetAsync(client, $"{address}/", null);
        Assert.Equal(HttpStatusCode.OK, rawStatus);
        Assert.Null(rawEncoding);
        Assert.Contains("zlmsharp 控制台 8f3a", rawBody);
    }

    [Fact]
    public async Task Etag_Returns304OnSecondRequest()
    {
        WritePage();
        var (app, address) = await StartHostAsync(_root);
        await using var _ = app;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var first = await client.GetAsync($"{address}/");
        var etag = first.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrEmpty(etag));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{address}/");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task Root_FallsBackToWebUiFolder_ForLegacyRootPath()
    {
        // 模拟升级前的 ini：rootPath 仍是旧默认值 ./www，此时应回退到程序目录下的 WebUI/
        var fallbackDir = Path.Combine(AppContext.BaseDirectory, "WebUI");
        Directory.CreateDirectory(fallbackDir);
        var fallbackPage = Path.Combine(fallbackDir, "index.html");
        var created = !File.Exists(fallbackPage);
        if (created)
        {
            await File.WriteAllTextAsync(fallbackPage, PageHtml, Encoding.UTF8);
        }

        try
        {
            var (app, address) = await StartHostAsync(Path.Combine(AppContext.BaseDirectory, "www"));
            await using var _ = app;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.GetAsync($"{address}/");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            if (created)
            {
                File.Delete(fallbackPage);
            }
        }
    }

    [Fact]
    public async Task AdminAliases_ServeSamePage_AndUnknownFileIs404()
    {
        WritePage();
        var (app, address) = await StartHostAsync(_root);
        await using var _ = app;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        foreach (var path in new[] { "/admin", "/admin/index.html" })
        {
            using var response = await client.GetAsync($"{address}{path}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("zlmsharp 控制台 8f3a", await response.Content.ReadAsStringAsync());
        }

        using var missing = await client.GetAsync($"{address}/admin/app.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task AdminPage_DoesNotShadowApiOrFlvRoutes()
    {
        WritePage();
        var (app, address) = await StartHostAsync(_root);
        await using var _ = app;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var version = await client.GetAsync($"{address}/index/api/version");
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Contains("\"code\":0", await version.Content.ReadAsStringAsync());

        // 未推流的 FLV 路径仍走 HTTP-FLV 端点（404 + 文本），不会被管理页面兜底成 HTML
        using var flv = await client.GetAsync($"{address}/live/missing.live.flv");
        Assert.Equal(HttpStatusCode.NotFound, flv.StatusCode);
        var body = await flv.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
    }

    private void WritePage(string? br = null, string? gzip = null)
    {
        var page = Path.Combine(_root, "index.html");
        File.WriteAllText(page, PageHtml, Encoding.UTF8);

        if (br is not null)
        {
            File.WriteAllBytes(page + ".br", Encoding.UTF8.GetBytes(br));
        }

        if (gzip is not null)
        {
            File.WriteAllBytes(page + ".gz", Encoding.UTF8.GetBytes(gzip));
        }
    }

    private static async Task<(HttpStatusCode Status, string? Encoding, string Body)> GetAsync(
        HttpClient client,
        string url,
        string? acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (acceptEncoding is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        }

        using var response = await client.SendAsync(request);
        var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
        return (response.StatusCode, encoding, await response.Content.ReadAsStringAsync());
    }

    private static async Task<(WebApplication App, string Address)> StartHostAsync(string rootPath)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var options = new AppOptions();
        options.Http.AllowOrigins = "*";
        options.Http.RootPath = rootPath;
        options.General.MaxStreamWaitMS = 500;
        options.General.WaitTrackReadyMS = 500;
        options.Rtsp.Port = 0;
        options.Rtmp.Port = 0;

        builder.Services.AddZlmSharp(o =>
        {
            o.Options = options;
            o.ConfigureLogging = false;
            o.RegisterStreamListeners = false;
        });

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
