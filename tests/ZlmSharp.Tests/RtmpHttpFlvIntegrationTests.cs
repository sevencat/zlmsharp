using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Protocols.Flv;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>端到端：RTMP 推流 → 内置监听服务 → HTTP-FLV 播放。</summary>
public class RtmpHttpFlvIntegrationTests
{
    [Fact]
    public async Task RtmpPublish_IsPlayableOverHttpFlv()
    {
        var rtmpPort = GetFreePort();
        var app = await StartHostAsync(rtmpPort);

        try
        {
            var address = GetAddress(app);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var publisher = await ConnectPublisherAsync(rtmpPort, cts.Token);

            try
            {
                await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
                await publisher.SendVideoAsync(
                    TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await client.GetAsync(
                    $"{address}/live/test.live.flv",
                    HttpCompletionOption.ResponseHeadersRead,
                    cts.Token);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("video/x-flv", response.Content.Headers.ContentType?.MediaType);

                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                var collected = new List<byte>();
                var buffer = new byte[8192];

                var tags = await ReadTagsAsync(stream, collected, buffer, minimumTags: 3, cts.Token);
                Assert.Equal(3, tags.Count);
                Assert.Equal(FlvTagType.Script, tags[0].Type);
                Assert.Equal(FlvTagType.Video, tags[1].Type);
                Assert.Equal(0, tags[1].AvcPacketType);
                Assert.True(tags[2].IsKeyFrame);
                Assert.Equal(0u, tags[2].Timestamp);

                await publisher.SendVideoAsync(
                    TestData.AnnexBInterFrame(), keyFrame: false, timestamp: 40, cancellationToken: cts.Token);

                tags = await ReadTagsAsync(stream, collected, buffer, minimumTags: 4, cts.Token);
                Assert.True(tags.Count >= 4);
                Assert.Equal(40u, tags[3].Timestamp);
                Assert.False(tags[3].IsKeyFrame);
            }
            finally
            {
                await publisher.DisposeAsync();
            }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<RtmpPublishClient> ConnectPublisherAsync(int rtmpPort, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RtmpPublishClient.ConnectAsync("127.0.0.1", rtmpPort, "live", "test", cancellationToken);
            }
            catch (SocketException) when (attempt < 20)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<WebApplication> StartHostAsync(int rtmpPort)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var options = new AppOptions();
        options.Http.AllowOrigins = "*";
        options.General.MaxStreamWaitMS = 2000;
        options.General.WaitTrackReadyMS = 2000;
        options.Rtsp.Port = 0;
        options.Rtmp.Port = rtmpPort;

        builder.Services.AddZlmSharp(o =>
        {
            o.Options = options;
            o.ConfigureLogging = false;
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

    private static async Task<List<FlvTag>> ReadTagsAsync(
        Stream stream,
        List<byte> collected,
        byte[] buffer,
        int minimumTags,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var tags = new List<FlvTag>();

        while (DateTime.UtcNow < deadline)
        {
            tags = FlvTagParser.ParseAll(collected.ToArray());
            if (tags.Count >= minimumTags)
            {
                return tags;
            }

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(TimeSpan.FromSeconds(2));

            int read;
            try
            {
                read = await stream.ReadAsync(buffer, readCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            collected.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return FlvTagParser.ParseAll(collected.ToArray());
    }
}
