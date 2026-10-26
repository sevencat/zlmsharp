using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>RTMP 转推 API 的入参、非法地址与离线流路径。</summary>
public class PushApiTests
{
    [Fact]
    public async Task StartPush_MissingUrl_IsRejected()
    {
        var app = await StartHostAsync();

        try
        {
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(
                await client.GetStringAsync($"{GetAddress(app)}/index/api/startPush?app=live&stream=cam"));

            Assert.Equal(-1, document.RootElement.GetProperty("code").GetInt32());
            Assert.Contains("url", document.RootElement.GetProperty("msg").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartPush_NonRtmpUrl_IsRejected()
    {
        var app = await StartHostAsync();

        try
        {
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(
                await client.GetStringAsync(
                    $"{GetAddress(app)}/index/api/startPush?app=live&stream=cam&url={Uri.EscapeDataString("http://example.com/live/x")}"));

            Assert.Equal(-1, document.RootElement.GetProperty("code").GetInt32());
            Assert.Contains("不是合法的 RTMP", document.RootElement.GetProperty("msg").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartPush_OfflineStream_IsRejected()
    {
        var app = await StartHostAsync();

        try
        {
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(
                await client.GetStringAsync(
                    $"{GetAddress(app)}/index/api/startPush?app=live&stream=cam&url={Uri.EscapeDataString("rtmp://127.0.0.1:1935/live/copy")}"));

            Assert.Equal(-1, document.RootElement.GetProperty("code").GetInt32());
            Assert.Contains("不在线", document.RootElement.GetProperty("msg").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetPushList_WithoutSessions_ReturnsEmptyList()
    {
        var app = await StartHostAsync();

        try
        {
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(
                await client.GetStringAsync($"{GetAddress(app)}/index/api/getPushList"));

            Assert.Equal(0, document.RootElement.GetProperty("code").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("data").GetArrayLength());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopPush_WithoutSession_ReturnsFalse()
    {
        var app = await StartHostAsync();

        try
        {
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(
                await client.GetStringAsync($"{GetAddress(app)}/index/api/stopPush?app=live&stream=cam"));

            Assert.Equal(0, document.RootElement.GetProperty("code").GetInt32());
            Assert.False(document.RootElement.GetProperty("data").GetProperty("flag").GetBoolean());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<WebApplication> StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var options = new AppOptions();
        options.Http.AllowOrigins = "*";

        builder.Services.AddZlmSharp(o =>
        {
            o.Options = options;
            o.ConfigureLogging = false;
            o.RegisterStreamListeners = false;
        });

        var app = builder.Build();
        app.UseZlmSharp();
        await app.StartAsync();
        return app;
    }

    private static string GetAddress(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses;

        return addresses.First().Replace("[::]", "127.0.0.1").TrimEnd('/');
    }
}
