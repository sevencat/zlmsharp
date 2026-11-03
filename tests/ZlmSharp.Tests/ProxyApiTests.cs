using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>拉流代理列表 API：添加后（含页面刷新的重复查询）列表不丢，删除后为空。</summary>
public class ProxyApiTests
{
    [Fact]
    public async Task GetProxyList_KeepsEntriesAcrossRefreshes()
    {
        var app = await StartHostAsync();

        try
        {
            using var client = new HttpClient();
            var address = GetAddress(app);

            using var add = JsonDocument.Parse(
                await client.GetStringAsync(
                    $"{address}/index/api/addStreamProxy?app=live&stream=cam&url={Uri.EscapeDataString("rtmp://127.0.0.1:9/live/src")}"));
            Assert.Equal(0, add.RootElement.GetProperty("code").GetInt32());
            var key = add.RootElement.GetProperty("data").GetProperty("key").GetString();
            Assert.Equal("__defaultVhost__/live/cam", key);

            // 模拟两次“页面刷新”：列表必须每次都能查到
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var list = JsonDocument.Parse(
                    await client.GetStringAsync($"{address}/index/api/getProxyList"));

                Assert.Equal(0, list.RootElement.GetProperty("code").GetInt32());
                var data = list.RootElement.GetProperty("data");
                Assert.Equal(1, data.GetArrayLength());
                Assert.Equal(key, data[0].GetProperty("key").GetString());
                Assert.Equal("cam", data[0].GetProperty("stream").GetString());
                Assert.Equal("rtmp://127.0.0.1:9/live/src", data[0].GetProperty("url").GetString());
                // 源地址连不上，流还没上线
                Assert.False(data[0].GetProperty("online").GetBoolean());
            }

            using var remove = JsonDocument.Parse(
                await client.GetStringAsync($"{address}/index/api/delStreamProxy?key={Uri.EscapeDataString(key!)}"));
            Assert.True(remove.RootElement.GetProperty("data").GetProperty("flag").GetBoolean());

            using var empty = JsonDocument.Parse(
                await client.GetStringAsync($"{address}/index/api/getProxyList"));
            Assert.Equal(0, empty.RootElement.GetProperty("data").GetArrayLength());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetProxyList_FiltersByStream()
    {
        var app = await StartHostAsync();

        try
        {
            using var client = new HttpClient();
            var address = GetAddress(app);

            await client.GetStringAsync(
                $"{address}/index/api/addStreamProxy?app=live&stream=a&url={Uri.EscapeDataString("rtmp://127.0.0.1:9/live/src")}");
            await client.GetStringAsync(
                $"{address}/index/api/addStreamProxy?app=live&stream=b&url={Uri.EscapeDataString("rtmp://127.0.0.1:9/live/src")}");

            using var list = JsonDocument.Parse(
                await client.GetStringAsync($"{address}/index/api/getProxyList?stream=b"));
            var data = list.RootElement.GetProperty("data");
            Assert.Equal(1, data.GetArrayLength());
            Assert.Equal("b", data[0].GetProperty("stream").GetString());
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
        options.Rtmp.Port = 0;
        options.Rtsp.Port = 0;

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
