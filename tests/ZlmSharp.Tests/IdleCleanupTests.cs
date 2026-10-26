using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Core;

namespace ZlmSharp.Tests;

/// <summary>无人观看回收定时器：auto_close 开关、残留流兜底、踢掉推流端。</summary>
public class IdleCleanupTests
{
    [Fact]
    public async Task AutoClose_ReclaimsStream_AndKicksPublisher()
    {
        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort, options =>
        {
            options.General.StreamNoneReaderDelayMS = 500;
            options.Protocol.AutoClose = true;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var publisher = await host.ConnectPublisherAsync(rtmpPort, cts.Token);

        try
        {
            await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
            await publisher.SendVideoAsync(
                TestData.AnnexBKeyFrame(),
                keyFrame: true,
                timestamp: 0,
                cancellationToken: cts.Token);

            var registry = host.Services.GetRequiredService<MediaSourceRegistry>();
            var sessions = host.Services.GetRequiredService<MediaSessionRegistry>();
            Assert.Equal(1, registry.Count);

            Assert.True(
                await WaitAsync(() => Task.FromResult(registry.Count == 0), TimeSpan.FromSeconds(8), cts.Token),
                "auto_close=1 时无人观看的流没有被回收");

            Assert.True(
                await WaitAsync(() => Task.FromResult(sessions.Count == 0), TimeSpan.FromSeconds(5), cts.Token),
                "回收流之后推流端会话仍在");
        }
        finally
        {
            await publisher.DisposeAsync();
        }
    }

    [Fact]
    public async Task WithoutAutoClose_KeepsPublishingStream()
    {
        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort, options =>
        {
            options.General.StreamNoneReaderDelayMS = 500;
            options.Protocol.AutoClose = false;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var publisher = await host.ConnectPublisherAsync(rtmpPort, cts.Token);
        await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
        await publisher.SendVideoAsync(
            TestData.AnnexBKeyFrame(),
            keyFrame: true,
            timestamp: 0,
            cancellationToken: cts.Token);

        var registry = host.Services.GetRequiredService<MediaSourceRegistry>();

        // 跨过至少两个扫描周期（2s/次）
        await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public async Task OrphanStream_WithoutPublisher_IsReclaimed()
    {
        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort, options =>
        {
            options.General.StreamNoneReaderDelayMS = 300;
            options.Protocol.AutoClose = false;
        });

        var registry = host.Services.GetRequiredService<MediaSourceRegistry>();
        registry.GetOrCreate(StreamKey.Create("__defaultVhost__", "live", "orphan"), "rtmp");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.True(
            await WaitAsync(() => Task.FromResult(registry.Count == 0), TimeSpan.FromSeconds(8), cts.Token),
            "没有发布者的残留流没有被回收");
    }

    [Fact]
    public async Task MediaList_ExposesDropFrames()
    {
        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var publisher = await host.ConnectPublisherAsync(rtmpPort, cts.Token);
        await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
        await publisher.SendVideoAsync(
            TestData.AnnexBKeyFrame(),
            keyFrame: true,
            timestamp: 0,
            cancellationToken: cts.Token);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var json = await client.GetStringAsync(
            $"{host.Address}/index/api/getMediaList?app=live&stream=test",
            cts.Token);

        Assert.Contains("\"dropFrames\":0", json);
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
