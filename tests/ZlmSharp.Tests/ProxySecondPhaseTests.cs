using System.Net;
using System.Net.Sockets;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Tests;

/// <summary>拉流代理二期：配置列表解析、并发上限、originType=proxy。</summary>
public class ProxySecondPhaseTests
{
    [Fact]
    public void ParseList_SupportsBothKeyForms()
    {
        var items = RtmpPullProxyManager.ParseList(
            "live/a=rtmp://host/live/a; __defaultVhost__/live/b=rtsp://host/live/b ;broken; live/c=");

        Assert.Equal(2, items.Count);
        Assert.Equal(StreamKey.Create(null, "live", "a"), items[0].Key);
        Assert.Equal("rtmp://host/live/a", items[0].Url);
        Assert.Equal(StreamKey.Create("__defaultVhost__", "live", "b"), items[1].Key);
        Assert.Equal("rtsp://host/live/b", items[1].Url);
    }

    [Fact]
    public async Task Start_RejectsUrlThatIsNeitherRtmpNorRtsp()
    {
        var manager = new RtmpPullProxyManager(new MediaSourceRegistry());

        try
        {
            Assert.Throws<ArgumentException>(() => manager.Start(null, "live", "a", "http://example.com/live/a"));
            Assert.Throws<ArgumentException>(() => manager.Start(null, "live", "a", "rtmp://host/onlyapp"));
        }
        finally
        {
            await manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task Start_EnforcesConcurrencyLimit()
    {
        var registry = new MediaSourceRegistry();
        await using var manager = new RtmpPullProxyManager(registry, maxConcurrency: 1);

        manager.Start(null, "live", "a", "rtmp://127.0.0.1:1/live/a");

        var exception = Assert.Throws<InvalidOperationException>(
            () => manager.Start(null, "live", "b", "rtmp://127.0.0.1:1/live/b"));

        Assert.Contains("并发上限", exception.Message);
        Assert.Equal(1, manager.Count);

        // 同一路重启不受上限影响
        manager.Start(null, "live", "a", "rtmp://127.0.0.1:1/live/a");
        Assert.Equal(1, manager.Count);

        manager.StopAll();
        Assert.Equal(0, manager.Count);
    }

    [Fact]
    public async Task ProxiedStream_ReportsOriginTypeProxy()
    {
        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var publisher = await host.ConnectPublisherAsync(rtmpPort, cts.Token);
        await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
        await publisher.SendVideoAsync(TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);

        var manager = host.Services.GetService(typeof(RtmpPullProxyManager)) as RtmpPullProxyManager;
        Assert.NotNull(manager);

        var origin = $"rtmp://127.0.0.1:{rtmpPort}/live/test";
        manager!.Start(null, "live", "proxied", origin);

        var key = StreamKey.Create(null, "live", "proxied");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        MediaSource? source = null;
        while (DateTime.UtcNow < deadline)
        {
            if (host.Services.GetService(typeof(MediaSourceRegistry)) is MediaSourceRegistry registry &&
                registry.TryGet(key, out var found) &&
                found is { Video.Width: > 0 })
            {
                source = found;
                break;
            }

            await Task.Delay(50, cts.Token);
        }

        Assert.NotNull(source);
        Assert.Equal("proxy", source!.OriginType);
        Assert.Equal("rtmp", source.Schema);

        manager.Stop(key);
    }
}
