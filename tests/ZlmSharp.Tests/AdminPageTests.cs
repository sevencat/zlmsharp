using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>内置管理页面：静态资源可访问，且不遮蔽 API / HTTP-FLV 路由。</summary>
public class AdminPageTests
{
    [Fact]
    public async Task Root_ReturnsBuiltInConsolePage()
    {
        var (app, address) = await StartHostAsync();
        await using var _ = app;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync($"{address}/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("zlmsharp 控制台", html);
        Assert.Contains("/admin/app.js", html);
    }

    [Fact]
    public async Task AdminAssets_AreServedFromEmbeddedResources()
    {
        var (app, address) = await StartHostAsync();
        await using var _ = app;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var script = await client.GetAsync($"{address}/admin/app.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Contains("analyzeResult", await script.Content.ReadAsStringAsync());

        var style = await client.GetAsync($"{address}/admin/style.css");
        Assert.Equal(HttpStatusCode.OK, style.StatusCode);
        Assert.Equal("text/css", style.Content.Headers.ContentType?.MediaType);

        var missing = await client.GetAsync($"{address}/admin/nope.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task AdminPage_DoesNotShadowApiOrFlvRoutes()
    {
        var (app, address) = await StartHostAsync();
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

    private static async Task<(WebApplication App, string Address)> StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var options = new AppOptions();
        options.Http.AllowOrigins = "*";
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
