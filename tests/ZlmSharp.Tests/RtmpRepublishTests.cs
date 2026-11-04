using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>
/// 同一 key 反复推流（叠加流重启、转推重连）：旧会话收尾不能把新推流者的 MediaSource 摘掉，
/// 否则表现为「叠加流消失且再也起不来」。
/// </summary>
public class RtmpRepublishTests
{
    [Fact]
    public void Remove_WithStaleInstance_KeepsNewSource()
    {
        var registry = new MediaSourceRegistry();
        var key = StreamKey.Create(null, "live", "cam");

        var first = registry.GetOrCreate(key, "rtmp");
        Assert.True(registry.Remove(key, first));
        Assert.True(first.IsClosed);

        var second = registry.GetOrCreate(key, "rtmp");
        Assert.NotSame(first, second);

        // 旧会话再收尾一次（它持有的是 first），不能误删 second
        Assert.False(registry.Remove(key, first));
        Assert.Same(second, registry.Find("", "live", "cam"));
        Assert.False(second.IsClosed);
    }

    [Fact]
    public async Task Republish_SameKey_SourceStaysOnline()
    {
        var rtmpPort = GetFreePort();
        var app = await StartHostAsync(rtmpPort);

        try
        {
            var registry = app.Services.GetRequiredService<MediaSourceRegistry>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // 第一次推流
            var first = await ConnectPublisherAsync(rtmpPort, "live", "cam", cts.Token);
            await first.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
            await first.SendVideoAsync(TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);
            Assert.NotNull(registry.Find("", "live", "cam"));

            // 模拟叠加流重启：停掉旧推流后立即用同一 key 重连
            await first.DisposeAsync();

            var second = await ConnectPublisherAsync(rtmpPort, "live", "cam", cts.Token);
            await second.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
            await second.SendVideoAsync(TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);

            // 等旧会话收尾，新推流的 source 必须仍然在线且留在注册表里
            var deadline = DateTime.UtcNow.AddSeconds(5);
            MediaSource? source = null;
            while (DateTime.UtcNow < deadline)
            {
                source = registry.Find("", "live", "cam");
                if (source is { IsClosed: false })
                {
                    break;
                }

                await Task.Delay(50, cts.Token);
            }

            Assert.NotNull(source);
            Assert.False(source!.IsClosed);

            // 还能写入新帧（说明新推流没有被写到已关闭的流上）
            await second.SendVideoAsync(
                TestData.AnnexBInterFrame(), keyFrame: false, timestamp: 40, cancellationToken: cts.Token);
            await Task.Delay(100, cts.Token);

            source = registry.Find("", "live", "cam");
            Assert.NotNull(source);
            Assert.False(source!.IsClosed);
            Assert.True(source.Video.Ready, "新推流的配置帧没有进入 MediaSource");

            await second.DisposeAsync();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<RtmpPublishClient> ConnectPublisherAsync(
        int rtmpPort,
        string app,
        string stream,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RtmpPublishClient.ConnectAsync("127.0.0.1", rtmpPort, app, stream, cancellationToken);
            }
            catch (SocketException) when (attempt < 20)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
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

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
