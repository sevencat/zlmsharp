using System.Buffers.Binary;
using ZlmSharp.Codecs;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Tests;

/// <summary>拉流代理二期：RTSP 源端到端（RTSP 推流 → 代理拉流 → 本地 MediaSource）。</summary>
public class RtspPullProxyTests
{
    [Fact]
    public async Task ProxyPullsRtspSource_IntoLocalStream()
    {
        var rtmpPort = TestHost.GetFreePort();
        var rtspPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort, options => options.Rtsp.Port = rtspPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var key = StreamKey.Create(null, "live", "rtsp-source");
        var uri = $"rtsp://127.0.0.1:{rtspPort}/live/rtsp-source";

        var client = await RtspTestClient.ConnectAsync("127.0.0.1", rtspPort, cts.Token);
        try
        {
            await client.SendAsync(
                "ANNOUNCE",
                uri,
                BuildSdp(),
                cts.Token,
                ("Content-Type", "application/sdp"));
            Assert.Equal(200, (await client.ReadResponseAsync(cts.Token)).StatusCode);

            await client.SendAsync(
                "SETUP",
                uri + "/trackID=0",
                cts.Token,
                ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
            Assert.Equal(200, (await client.ReadResponseAsync(cts.Token)).StatusCode);

            await client.SendAsync("RECORD", uri, cts.Token, ("Session", "pull-test"));
            Assert.Equal(200, (await client.ReadResponseAsync(cts.Token)).StatusCode);

            // 持续推帧（30fps），供代理拉取
            var pushTask = Task.Run(() => PushAsync(client, cts.Token), cts.Token);

            var manager = (RtmpPullProxyManager)host.Services.GetService(typeof(RtmpPullProxyManager))!;
            var registry = (MediaSourceRegistry)host.Services.GetService(typeof(MediaSourceRegistry))!;

            manager.Start(null, "live", "rtsp-proxied", uri);

            var proxied = StreamKey.Create(null, "live", "rtsp-proxied");
            Assert.True(
                await WaitAsync(
                    () => Task.FromResult(
                        registry.TryGet(proxied, out var source) && source is { Video.Width: > 0 }),
                    TimeSpan.FromSeconds(15),
                    cts.Token),
                "代理没有从 RTSP 源拉到流");

            registry.TryGet(proxied, out var pulled);
            Assert.NotNull(pulled);
            Assert.Equal("proxy", pulled!.OriginType);
            Assert.Equal("rtsp", pulled.Schema);

            // 等一帧实际数据，确认 RTP 解包链路通了
            Assert.True(
                await WaitAsync(
                    () => Task.FromResult(HasFrames(pulled)),
                    TimeSpan.FromSeconds(15),
                    cts.Token),
                "代理流没有收到任何帧");

            manager.Stop(proxied);

            cts.Cancel();
            try
            {
                await pushTask;
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    /// <summary><c>[proxy] enable=1</c> + <c>list</c>：宿主启动即拉，源流后到也能靠重连接上。</summary>
    [Fact]
    public async Task ConfiguredProxy_StartsPullingOnHostStart()
    {
        var rtmpPort = TestHost.GetFreePort();
        var rtspPort = TestHost.GetFreePort();
        var origin = $"rtsp://127.0.0.1:{rtspPort}/live/auto-src";

        await using var host = await TestHost.StartAsync(
            rtmpPort,
            options =>
            {
                options.Rtsp.Port = rtspPort;
                options.Proxy.Enable = true;
                options.Proxy.RetryDelayMS = 200;
                options.Proxy.List = $"live/auto={origin}";
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var manager = (RtmpPullProxyManager)host.Services.GetService(typeof(RtmpPullProxyManager))!;
        var registry = (MediaSourceRegistry)host.Services.GetService(typeof(MediaSourceRegistry))!;
        var proxied = StreamKey.Create(null, "live", "auto");

        Assert.True(manager.IsPulling(proxied), "[proxy] enable=1 没有在宿主启动时发起拉流");

        // 源流在宿主启动之后才出现
        var client = await RtspTestClient.ConnectAsync("127.0.0.1", rtspPort, cts.Token);
        try
        {
            await client.SendAsync(
                "ANNOUNCE",
                origin,
                BuildSdp(),
                cts.Token,
                ("Content-Type", "application/sdp"));
            Assert.Equal(200, (await client.ReadResponseAsync(cts.Token)).StatusCode);

            await client.SendAsync(
                "SETUP",
                origin + "/trackID=0",
                cts.Token,
                ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
            Assert.Equal(200, (await client.ReadResponseAsync(cts.Token)).StatusCode);

            await client.SendAsync("RECORD", origin, cts.Token, ("Session", "auto-test"));
            Assert.Equal(200, (await client.ReadResponseAsync(cts.Token)).StatusCode);

            var pushTask = Task.Run(() => PushAsync(client, cts.Token), cts.Token);

            Assert.True(
                await WaitAsync(
                    () =>
                    {
                        if (!registry.TryGet(proxied, out var source) || source is not { Video.Width: > 0 })
                        {
                            return Task.FromResult(false);
                        }

                        return Task.FromResult(HasFrames(source));
                    },
                    TimeSpan.FromSeconds(15),
                    cts.Token),
                "配置的启动即拉没有从 RTSP 源拉到流");

            registry.TryGet(proxied, out var pulled);
            Assert.NotNull(pulled);
            Assert.Equal("proxy", pulled!.OriginType);
            Assert.Equal("rtsp", pulled.Schema);

            cts.Cancel();
            try
            {
                await pushTask;
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            manager.Stop(proxied);
            await client.DisposeAsync();
        }
    }

    private static async Task PushAsync(RtspTestClient client, CancellationToken cancellationToken)
    {
        var sequence = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var nal in AnnexB.Split(TestData.AnnexBKeyFrame()))
                {
                    await client.SendInterleavedAsync(
                        0,
                        BuildRtp(96, (ushort)++sequence, (uint)(sequence * 3000), nal),
                        cancellationToken);
                }

                await Task.Delay(40, cancellationToken);
            }
        }
        catch (Exception)
        {
            // 测试收尾时取消
        }
    }

    private static bool HasFrames(MediaSource source)
    {
        var gop = source.SnapshotGop();
        foreach (var frame in gop)
        {
            frame.Dispose();
        }

        return gop.Count > 0;
    }

    private static string BuildSdp()
    {
        var sps = Convert.ToBase64String(TestData.Sps720);
        var pps = Convert.ToBase64String(TestData.Pps);
        return $"""
            v=0
            o=- 0 0 IN IP4 127.0.0.1
            s=test
            c=IN IP4 127.0.0.1
            t=0 0
            m=video 0 RTP/AVP 96
            a=rtpmap:96 H264/90000
            a=fmtp:96 packetization-mode=1;sprop-parameter-sets={sps},{pps}
            a=control:trackID=0
            """;
    }

    private static byte[] BuildRtp(int payloadType, ushort sequence, uint timestamp, byte[] payload)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = (byte)(0x80 | (payloadType & 0x7F)); // marker=1
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0x11223344);
        payload.CopyTo(packet, 12);
        return packet;
    }

    private static async Task<bool> WaitAsync(
        Func<Task<bool>> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (await probe())
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return false;
    }
}
