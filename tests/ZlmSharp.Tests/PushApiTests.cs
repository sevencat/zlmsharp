using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Codecs;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
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

    [Fact]
    public async Task StartPush_ThenGetListTwice_SessionSurvives()
    {
        var app = await StartHostAsync();

        try
        {
            var registry = app.Services.GetRequiredService<MediaSourceRegistry>();
            var source = registry.GetOrCreate(StreamKey.Create(null, "live", "cam"), "rtmp");
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBConfig(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Config | FrameFlags.Key));
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBKeyFrame(), TrackType.Video, CodecId.H264, 1000, 1000, FrameFlags.Key));
            // 真实推流由 FlvFrameIngest 设置参数集；测试里直接补上
            source.Video.SetH264Config(TestData.Sps720, TestData.Pps);

            using var client = new HttpClient();

            // 目标地址连不上没关系：会话仍应保留（state=failed 也要能看到）
            using var start = JsonDocument.Parse(
                await client.GetStringAsync(
                    $"{GetAddress(app)}/index/api/startPush?app=live&stream=cam&url={Uri.EscapeDataString("rtmp://127.0.0.1:9/live/relay")}"));
            Assert.True(
                start.RootElement.GetProperty("code").GetInt32() == 0,
                start.RootElement.GetProperty("msg").GetString());

            // 模拟页面刷新：重新查询列表，会话必须还在
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var list = JsonDocument.Parse(
                    await client.GetStringAsync($"{GetAddress(app)}/index/api/getPushList"));

                Assert.Equal(0, list.RootElement.GetProperty("code").GetInt32());
                var data = list.RootElement.GetProperty("data");
                Assert.Equal(1, data.GetArrayLength());
                Assert.Equal("cam", data[0].GetProperty("stream").GetString());
            }
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
