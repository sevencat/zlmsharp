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

/// <summary>媒体状态 API：getMediaList / close_streams。</summary>
public class ApiMediaIntegrationTests
{
    [Fact]
    public async Task GetMediaList_ReportsOnlineStream()
    {
        var app = await StartHostAsync();

        try
        {
            var registry = app.Services.GetRequiredService<MediaSourceRegistry>();
            var source = registry.GetOrCreate(StreamKey.Create(null, "live", "test"), "rtmp");
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBConfig(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Config | FrameFlags.Key));
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBKeyFrame(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Key));

            using var client = new HttpClient();
            var json = await client.GetStringAsync($"{GetAddress(app)}/index/api/getMediaList?app=live");

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(0, root.GetProperty("code").GetInt32());

            var data = root.GetProperty("data");
            Assert.Equal(1, data.GetArrayLength());

            var item = data[0];
            Assert.Equal("live", item.GetProperty("app").GetString());
            Assert.Equal("test", item.GetProperty("stream").GetString());
            Assert.Equal("rtmp", item.GetProperty("schema").GetString());
            Assert.Equal("H264", item.GetProperty("videoCodec").GetString());
            Assert.True(item.GetProperty("videoWidth").GetInt32() > 0, "应从 SPS 回填宽度");
            Assert.Equal(0, item.GetProperty("readerCount").GetInt32());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task CloseStreams_RemovesMatchingSource()
    {
        var app = await StartHostAsync();

        try
        {
            var registry = app.Services.GetRequiredService<MediaSourceRegistry>();
            var key = StreamKey.Create(null, "live", "test");
            var source = registry.GetOrCreate(key, "rtmp");
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBConfig(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Config | FrameFlags.Key));

            using var client = new HttpClient();
            var json = await client.GetStringAsync($"{GetAddress(app)}/index/api/close_streams?stream=test");

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(0, root.GetProperty("code").GetInt32());
            Assert.Equal(1, root.GetProperty("count").GetInt32());
            Assert.True(root.GetProperty("closed").GetBoolean());

            Assert.False(registry.TryGet(key, out _), "close_streams 应移除流");
            Assert.True(source.IsClosed);
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
        options.General.MaxStreamWaitMS = 2000;
        options.General.WaitTrackReadyMS = 2000;

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
