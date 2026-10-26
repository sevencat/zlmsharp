using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Flv;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

public class HttpFlvIntegrationTests
{
    [Fact]
    public async Task HttpFlv_StreamsHeaderMetadataAndTags()
    {
        var app = await StartHostAsync();
        try
        {
            var address = GetAddress(app);
            var registry = app.Services.GetRequiredService<MediaSourceRegistry>();
            var source = registry.GetOrCreate(
                StreamKey.Create(StreamKey.DefaultVhost, "live", "test"), "live");

            PublishConfigAndKeyFrame(source);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // API 端点仍然可用（literal 路由优先于 catch-all）
            var versionResponse = await client.GetAsync($"{address}/index/api/version");
            Assert.Equal(HttpStatusCode.OK, versionResponse.StatusCode);
            Assert.Contains("\"code\":0", await versionResponse.Content.ReadAsStringAsync());

            using var response = await client.GetAsync(
                $"{address}/live/test.live.flv",
                HttpCompletionOption.ResponseHeadersRead);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("video/x-flv", response.Content.Headers.ContentType?.MediaType);

            await using var stream = await response.Content.ReadAsStreamAsync();
            var collected = new List<byte>();
            var buffer = new byte[8192];

            var tags = await ReadTagsAsync(stream, collected, buffer, minimumTags: 3);
            Assert.Equal(3, tags.Count);
            Assert.Equal(FlvTagType.Script, tags[0].Type);
            Assert.Equal(FlvTagType.Video, tags[1].Type);
            Assert.Equal(0, tags[1].AvcPacketType);
            Assert.Equal(FlvTagType.Video, tags[2].Type);
            Assert.True(tags[2].IsKeyFrame);
            Assert.Equal(0u, tags[2].Timestamp);

            // 推一帧非关键帧，应继续有新 tag
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBInterFrame(), TrackType.Video, CodecId.H264, 40, 40));

            tags = await ReadTagsAsync(stream, collected, buffer, minimumTags: 4);
            Assert.True(tags.Count >= 4);
            Assert.Equal(40u, tags[3].Timestamp);
            Assert.False(tags[3].IsKeyFrame);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task NonFlvPath_Returns404()
    {
        var app = await StartHostAsync();
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync($"{GetAddress(app)}/some/random/path");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<List<FlvTag>> ReadTagsAsync(
        Stream stream,
        List<byte> collected,
        byte[] buffer,
        int minimumTags)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var tags = new List<FlvTag>();

        while (DateTime.UtcNow < deadline)
        {
            tags = FlvTagParser.ParseAll(collected.ToArray());
            if (tags.Count >= minimumTags)
            {
                return tags;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var read = await stream.ReadAsync(buffer, cts.Token);
            if (read == 0)
            {
                break;
            }

            collected.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return FlvTagParser.ParseAll(collected.ToArray());
    }

    private static void PublishConfigAndKeyFrame(MediaSource source)
    {
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBConfig(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Config));
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBKeyFrame(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Key));
    }

    private static async Task<WebApplication> StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var options = new AppOptions();
        options.Http.AllowOrigins = "*";
        options.General.MaxStreamWaitMS = 2000;
        options.General.WaitTrackReadyMS = 2000;
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
