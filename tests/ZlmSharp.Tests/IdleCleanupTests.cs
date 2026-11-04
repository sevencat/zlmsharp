using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtmp;
using ZlmSharp.Recording;

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
    public async Task AutoClose_KeepsProxyStream_WhenKeepOnNoneReader()
    {
        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort, options =>
        {
            options.General.StreamNoneReaderDelayMS = 300;
            options.Protocol.AutoClose = true;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proxies = host.Services.GetRequiredService<RtmpPullProxyManager>();
        var key = proxies.Start(
            "__defaultVhost__",
            "live",
            "proxycam",
            $"rtmp://127.0.0.1:{TestHost.GetFreePort()}/live/nowhere",
            keepOnNoneReader: true);

        // 等两个扫描周期以上：显式常驻的代理不能被回收
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        var registry = host.Services.GetRequiredService<MediaSourceRegistry>();
        Assert.True(registry.TryGet(key, out var source), "常驻代理被无人观看回收误删");
        Assert.False(source!.IsClosed);
        Assert.True(proxies.IsKeptOnNoneReader(key));
    }

    [Fact]
    public async Task AutoClose_ReclaimsProxyStream_ByDefault()
    {
        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort, options =>
        {
            options.General.StreamNoneReaderDelayMS = 300;
            options.Protocol.AutoClose = true;
        });

        var registry = host.Services.GetRequiredService<MediaSourceRegistry>();
        registry.GetOrCreate(StreamKey.Create("__defaultVhost__", "live", "proxycam"), "rtmp", originType: "proxy");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Assert.True(
            await WaitAsync(() => Task.FromResult(registry.Count == 0), TimeSpan.FromSeconds(15), cts.Token),
            "没有设置 keep_on_none_reader 的代理流应按 auto_close 回收");
    }

    [Fact]
    public async Task AutoClose_KeepsRecordingStream()
    {
        var root = Path.Combine(Path.GetTempPath(), $"zlmsharp-rec-{Guid.NewGuid():N}");
        var rtmpPort = TestHost.GetFreePort();

        try
        {
            await using var host = await TestHost.StartAsync(rtmpPort, options =>
            {
                options.Env.RecordRoot = root;
                options.General.StreamNoneReaderDelayMS = 300;
                options.Protocol.AutoClose = true;
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var publisher = await host.ConnectPublisherAsync(rtmpPort, cts.Token);
            await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
            await publisher.SendVideoAsync(
                TestData.AnnexBKeyFrame(),
                keyFrame: true,
                timestamp: 0,
                cancellationToken: cts.Token);

            var key = StreamKey.Create("__defaultVhost__", "live", "test");
            var recorder = host.Services.GetRequiredService<RecorderManager>();
            recorder.Start(key, RecordFormat.Mp4);
            Assert.True(recorder.Get(key)?.Recording == true, "录像没有启动");

            // 超过两个扫描周期：录制中的流无人观看也不能被回收
            await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

            var registry = host.Services.GetRequiredService<MediaSourceRegistry>();
            Assert.True(registry.TryGet(key, out var source), "录制中的流被无人观看回收误删");
            Assert.False(source!.IsClosed);

            // 停录后恢复回收（录制 reader 必须被摘掉，否则 ReaderCount 不归零、永远回收不了）
            Assert.True(recorder.Stop(key.Vhost, key.App, key.Stream));
            Assert.Equal(0, source!.ReaderCount);
            Assert.True(
                await WaitAsync(() => Task.FromResult(registry.Count == 0), TimeSpan.FromSeconds(15), cts.Token),
                "停止录像后无人观看的流应被回收");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
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
