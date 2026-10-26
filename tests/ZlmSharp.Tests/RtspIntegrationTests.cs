using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>端到端：内置 RTSP 监听服务（DI 装配）→ DESCRIBE/SETUP/PLAY → 收到 RTP。</summary>
public class RtspIntegrationTests
{
    [Fact]
    public async Task RtspListener_PlaysRegisteredStream()
    {
        var rtspPort = GetFreePort();
        var app = await StartHostAsync(rtspPort);

        try
        {
            var registry = app.Services.GetRequiredService<MediaSourceRegistry>();
            var source = registry.GetOrCreate(StreamKey.Create(null, "live", "test"), "rtmp");
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBConfig(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Config | FrameFlags.Key));
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBKeyFrame(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Key));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var client = await ConnectWithRetryAsync(rtspPort, cts.Token);
            var baseUri = $"rtsp://127.0.0.1:{rtspPort}/live/test";

            try
            {
                await client.SendAsync("DESCRIBE", baseUri, cts.Token, ("Accept", "application/sdp"));
                var describe = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, describe.StatusCode);
                Assert.Contains("sprop-parameter-sets=", describe.Body);

                await client.SendAsync(
                    "SETUP",
                    baseUri + "/trackID=0",
                    cts.Token,
                    ("Transport", "RTP/AVP/TCP;unicast;interleaved=2-3"));
                var setup = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, setup.StatusCode);
                Assert.Contains("interleaved=2-3", setup.Header("Transport")!);

                await client.SendAsync("PLAY", baseUri + "/trackID=0", cts.Token);
                var play = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, play.StatusCode);

                var (channel, packet) = await client.ReadInterleavedAsync(cts.Token);
                Assert.Equal(2, channel);
                Assert.Equal(0x65, packet[12]);
                Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
            }
            finally
            {
                await client.DisposeAsync();
            }

            // 会话清理是异步的，轮询等待观看者归零
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (source.ReaderCount > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.Equal(0, source.ReaderCount);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task RtspListener_UdpTransport_PlaysRegisteredStream()
    {
        var rtspPort = GetFreePort();
        var app = await StartHostAsync(rtspPort);

        using var rtpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var rtcpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var rtpPort = ((IPEndPoint)rtpClient.Client.LocalEndPoint!).Port;
        var rtcpPort = ((IPEndPoint)rtcpClient.Client.LocalEndPoint!).Port;

        try
        {
            var registry = app.Services.GetRequiredService<MediaSourceRegistry>();
            var source = registry.GetOrCreate(StreamKey.Create(null, "live", "udp"), "rtmp");
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBConfig(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Config | FrameFlags.Key));
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBKeyFrame(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Key));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var client = await ConnectWithRetryAsync(rtspPort, cts.Token);
            var baseUri = $"rtsp://127.0.0.1:{rtspPort}/live/udp";

            try
            {
                await client.SendAsync(
                    "SETUP",
                    baseUri + "/trackID=0",
                    cts.Token,
                    ("Transport", $"RTP/AVP;unicast;client_port={rtpPort}-{rtcpPort}"));
                var setup = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, setup.StatusCode);
                Assert.Contains("server_port=", setup.Header("Transport")!);

                await client.SendAsync("PLAY", baseUri + "/trackID=0", cts.Token);
                var play = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, play.StatusCode);

                var packet = (await rtpClient.ReceiveAsync(cts.Token)).Buffer;
                Assert.Equal(0x65, packet[12]);

                var senderReport = (await rtcpClient.ReceiveAsync(cts.Token)).Buffer;
                Assert.Equal(200, senderReport[1]);
            }
            finally
            {
                await client.DisposeAsync();
            }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<RtspTestClient> ConnectWithRetryAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RtspTestClient.ConnectAsync("127.0.0.1", port, cancellationToken);
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

    private static async Task<WebApplication> StartHostAsync(int rtspPort)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var options = new AppOptions();
        options.Http.AllowOrigins = "*";
        options.General.MaxStreamWaitMS = 2000;
        options.General.WaitTrackReadyMS = 2000;
        options.Rtmp.Port = 0;
        options.Rtsp.Port = rtspPort;

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
}
