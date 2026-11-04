using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Ai;
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

    [Fact]
    public async Task CloseStreams_SkipsProxyAndOverlayStreams()
    {
        var app = await StartHostAsync(new SuffixOverlayController());

        try
        {
            var registry = app.Services.GetRequiredService<MediaSourceRegistry>();
            var proxyKey = StreamKey.Create(null, "live", "proxycam");
            var proxy = registry.GetOrCreate(proxyKey, "rtmp", originType: "proxy");
            var overlayKey = StreamKey.Create(null, "live", "cam_ai");
            var overlay = registry.GetOrCreate(overlayKey, "rtmp");
            var normalKey = StreamKey.Create(null, "live", "normal");
            var normal = registry.GetOrCreate(normalKey, "rtmp");

            using var client = new HttpClient();
            var json = await client.GetStringAsync($"{GetAddress(app)}/index/api/close_streams?app=live");

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(0, root.GetProperty("code").GetInt32());
            Assert.Equal(1, root.GetProperty("count").GetInt32());

            Assert.False(proxy.IsClosed, "代理流不能被 close_streams 关闭（只能 delStreamProxy）");
            Assert.False(overlay.IsClosed, "叠加流不能被 close_streams 关闭（只能 stopOverlay）");
            Assert.True(normal.IsClosed, "普通流应被关闭");
            Assert.True(registry.TryGet(proxyKey, out _));
            Assert.True(registry.TryGet(overlayKey, out _));
            Assert.False(registry.TryGet(normalKey, out _));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddStreamProxy_KeepFlag_RoundTrips()
    {
        var app = await StartHostAsync();

        try
        {
            using var client = new HttpClient();
            var url = Uri.EscapeDataString("rtmp://127.0.0.1:1/live/nowhere");

            var keep = await client.GetStringAsync(
                $"{GetAddress(app)}/index/api/addStreamProxy?app=live&stream=keepcam&url={url}&keep=1");
            Assert.Contains("\"code\":0", keep);

            // 不传 keep = 普通代理，没人看时允许回收
            var plain = await client.GetStringAsync(
                $"{GetAddress(app)}/index/api/addStreamProxy?app=live&stream=plaincam&url={url}");
            Assert.Contains("\"code\":0", plain);

            var list = await client.GetStringAsync($"{GetAddress(app)}/index/api/getProxyList?app=live");
            using var document = JsonDocument.Parse(list);
            var items = document.RootElement.GetProperty("data").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.True(items.Single(item => item.GetProperty("stream").GetString() == "keepcam").GetProperty("keepOnNoneReader").GetBoolean());
            Assert.False(items.Single(item => item.GetProperty("stream").GetString() == "plaincam").GetProperty("keepOnNoneReader").GetBoolean());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>只实现 <c>_ai</c> 后缀识别的替身，用于验证 close_streams 的受管流保护。</summary>
    private sealed class SuffixOverlayController : IOverlayController
    {
        public string OverlayStreamName(string stream) => stream + "_ai";

        public bool IsOverlayStream(string? stream) =>
            stream?.EndsWith("_ai", StringComparison.OrdinalIgnoreCase) == true;

        public OverlayStatus Start(OverlayRequest request) => throw new NotSupportedException();

        public bool Stop(string vhost, string app, string stream) => false;

        public OverlayStatus? Get(string vhost, string app, string stream) => null;

        public IReadOnlyList<OverlayStatus> List(string vhost = "", string app = "", string stream = "") => [];
    }

    private static async Task<WebApplication> StartHostAsync(IOverlayController? overlay = null)
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

        if (overlay is not null)
        {
            builder.Services.AddSingleton(overlay);
        }

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
