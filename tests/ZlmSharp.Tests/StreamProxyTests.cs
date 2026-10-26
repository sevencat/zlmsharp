using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtmp;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>拉流代理：远端 RTMP → 本地 MediaSource → 本地可播放。</summary>
public class StreamProxyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AddStreamProxy_MakesRemoteRtmpPlayableLocally()
    {
        var rtmpPort = GetFreePort();
        var app = await StartHostAsync(rtmpPort);
        var registry = app.Services.GetRequiredService<MediaSourceRegistry>();

        using var cts = new CancellationTokenSource(Timeout);
        var publisher = await RtmpPublishClient.ConnectAsync("127.0.0.1", rtmpPort, "live", "src", cts.Token);

        try
        {
            await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
            await publisher.SendVideoAsync(
                TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);

            var origin = $"rtmp://127.0.0.1:{rtmpPort}/live/src";
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync(
                $"{GetAddress(app)}/index/api/addStreamProxy?app=live&stream=proxy&url={Uri.EscapeDataString(origin)}",
                cts.Token));

            Assert.Equal(0, document.RootElement.GetProperty("code").GetInt32());
            var key = document.RootElement.GetProperty("data").GetProperty("key").GetString();
            Assert.Equal(StreamKey.Create(null, "live", "proxy").ToString(), key);

            // 代理流就绪：本地有了 live/proxy，且已从远端拿到 SPS/PPS
            var source = await WaitForSourceAsync(registry, StreamKey.Create(null, "live", "proxy"), cts.Token);
            Assert.True(source.Video.Width > 0, "代理流应带上远端 SPS 解析出的宽高");

            // 本地直接播放代理流（等价于 ffplay rtmp://127.0.0.1:1935/live/proxy）
            var player = await RtmpPlayClient.ConnectAsync("127.0.0.1", rtmpPort, "live", cts.Token);
            try
            {
                await player.PlayAsync("proxy", cts.Token);
                // 顺序是 Play.Reset → Play.Start，等到 Start 再读数据
                var code = string.Empty;
                while (code != "NetStream.Play.Start")
                {
                    (code, _) = await player.ReadNextStatusAsync(cts.Token);
                }

                var keyFrame = await ReadKeyFrameAsync(player, cts.Token);
                Assert.NotNull(keyFrame);
                Assert.Equal(0x17, keyFrame![0] & 0xFF); // keyframe + AVC
                Assert.Equal(1, keyFrame[1]);            // AVC NALU
            }
            finally
            {
                await player.DisposeAsync();
            }

            // 停止代理：本地流随之移除
            using var stopped = JsonDocument.Parse(await client.GetStringAsync(
                $"{GetAddress(app)}/index/api/delStreamProxy?{Uri.EscapeDataString("key")}={Uri.EscapeDataString(key!)}",
                cts.Token));
            Assert.True(stopped.RootElement.GetProperty("data").GetProperty("flag").GetBoolean());

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (registry.TryGet(StreamKey.Create(null, "live", "proxy"), out _) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, cts.Token);
            }

            Assert.False(registry.TryGet(StreamKey.Create(null, "live", "proxy"), out _));
        }
        finally
        {
            await publisher.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<byte[]?> ReadKeyFrameAsync(RtmpPlayClient player, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await player.ReadMessageAsync(cancellationToken);
            Assert.NotNull(message);
            // 0x17 = keyframe + AVC，payload[1]=1 表示 AVC NALU（0 是 sequence header）
            if (message!.Type == RtmpMessageType.Video &&
                message.Payload.Length > 2 &&
                (message.Payload[0] & 0x0F) == 7 &&
                message.Payload[1] == 1)
            {
                return message.Payload;
            }
        }

        return null;
    }

    private static async Task<MediaSource> WaitForSourceAsync(
        MediaSourceRegistry registry,
        StreamKey key,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (registry.TryGet(key, out var source) && source is { IsClosed: false, Video.Width: > 0 })
            {
                return source!;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException($"等待代理流就绪超时：{key}");
    }

    private static async Task<WebApplication> StartHostAsync(int rtmpPort)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var options = new AppOptions();
        options.Http.AllowOrigins = "*";
        options.General.MaxStreamWaitMS = 2000;
        options.General.WaitTrackReadyMS = 2000;
        options.Rtmp.Port = rtmpPort;
        options.Rtsp.Port = 0;

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

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
