using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>录像 API 的入参/离线流路径（不真正落盘）。</summary>
public class RecordApiTests
{
    [Fact]
    public async Task StartRecord_MissingStream_IsRejected()
    {
        var app = await StartHostAsync();

        try
        {
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(
                await client.GetStringAsync($"{GetAddress(app)}/index/api/startRecord?app=live"));
            var root = document.RootElement;

            Assert.Equal(-1, root.GetProperty("code").GetInt32());
            Assert.Contains("app 与 stream", root.GetProperty("msg").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartRecord_OfflineStream_IsRejected()
    {
        var app = await StartHostAsync();

        try
        {
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(
                await client.GetStringAsync($"{GetAddress(app)}/index/api/startRecord?app=live&stream=cam&type=3"));
            var root = document.RootElement;

            Assert.Equal(-1, root.GetProperty("code").GetInt32());
            Assert.Contains("不在线", root.GetProperty("msg").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task IsRecording_WithoutSession_ReturnsNullData()
    {
        var app = await StartHostAsync();

        try
        {
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(
                await client.GetStringAsync($"{GetAddress(app)}/index/api/isRecording?app=live&stream=cam"));
            var root = document.RootElement;

            Assert.Equal(0, root.GetProperty("code").GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopRecord_WithoutSession_ReturnsFalse()
    {
        var app = await StartHostAsync();

        try
        {
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(
                await client.GetStringAsync($"{GetAddress(app)}/index/api/stopRecord?app=live&stream=cam"));

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
