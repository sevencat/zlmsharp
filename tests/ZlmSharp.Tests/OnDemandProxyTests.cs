using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtmp;
using ZlmSharp.Server.Net;

namespace ZlmSharp.Tests;

/// <summary>
/// 按需拉流：配置 <c>[proxy] on_demand_list</c> 后平时不拉流，
/// 播放端点播才拉起，观众走光自动停止；断流只要还有观众就一直重连。
/// </summary>
public class OnDemandProxyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void Policy_KeepsPullingWhileSomeoneWatches()
    {
        Assert.False(OnDemandProxyPolicy.ShouldStop(
            readerCount: 1,
            totalReaderCount: 5,
            idleFor: TimeSpan.FromMinutes(10),
            sinceStart: TimeSpan.FromMinutes(30),
            idleDelay: TimeSpan.FromSeconds(5),
            maxWait: TimeSpan.FromSeconds(30),
            out _));
    }

    [Fact]
    public void Policy_StopsOnlyAfterIdleWhenSomeoneWatched()
    {
        Assert.False(OnDemandProxyPolicy.ShouldStop(
            0, 3, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), out _));

        Assert.True(OnDemandProxyPolicy.ShouldStop(
            0, 3, TimeSpan.FromSeconds(6), TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), out var reason));
        Assert.Contains("无人观看", reason);
    }

    [Fact]
    public void Policy_StopsWhenNobodyEverWatchedAndMaxWaitExceeded()
    {
        // 观众还在等待流上线（reader 尚未建立）：要有耐心
        Assert.False(OnDemandProxyPolicy.ShouldStop(
            0, 0, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), out _));

        Assert.True(OnDemandProxyPolicy.ShouldStop(
            0, 0, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(31),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), out var reason));
        Assert.Contains("仍无观众", reason);
    }

    [Fact]
    public void Config_BindsOnDemandKeys()
    {
        var document = IniDocument.Parse(DefaultConfig.Text);
        document.Set("proxy", "on_demand_list", "live/cam1=rtsp://1.2.3.4/live/1;live/cam2=rtmp://5.6.7.8/live/2");
        document.Set("proxy", "on_demand_idle_ms", "3000");
        document.Set("proxy", "on_demand_max_wait_ms", "45000");

        var options = ConfigBinder.Bind(document);

        Assert.Equal("live/cam1=rtsp://1.2.3.4/live/1;live/cam2=rtmp://5.6.7.8/live/2", options.Proxy.OnDemandList);
        Assert.Equal(3000, options.Proxy.OnDemandIdleMS);
        Assert.Equal(45000, options.Proxy.OnDemandMaxWaitMS);

        var targets = RtmpPullProxyManager.ParseList(options.Proxy.OnDemandList);
        Assert.Equal(2, targets.Count);
        Assert.Equal(StreamKey.Create(null, "live", "cam1"), targets[0].Key);
        Assert.Equal("rtsp://1.2.3.4/live/1", targets[0].Url);
    }

    [Fact]
    public async Task PlayerRequest_StartsPull_AndStopsAfterViewersLeave()
    {
        var rtmpPort = TestHost.GetFreePort();
        var sourceKey = StreamKey.Create(null, "live", "od-src");
        var pullKey = StreamKey.Create(null, "live", "od");
        var origin = $"rtmp://127.0.0.1:{rtmpPort}/live/od-src";

        await using var host = await TestHost.StartAsync(rtmpPort, options =>
        {
            options.General.MaxStreamWaitMS = 15000;
            options.General.WaitTrackReadyMS = 15000;
            options.Proxy.RetryDelayMS = 200;
            options.Proxy.OnDemandList = $"live/od={origin}";
            options.Proxy.OnDemandIdleMS = 1000;
            options.Proxy.OnDemandMaxWaitMS = 20000;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var registry = host.Services.GetRequiredService<MediaSourceRegistry>();
        var manager = host.Services.GetRequiredService<RtmpPullProxyManager>();

        // 源流先推起来（按需代理要拉的远端）
        var publisher = await RtmpPublishClient.ConnectAsync("127.0.0.1", rtmpPort, "live", "od-src", cts.Token);
        try
        {
            await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
            await publisher.SendVideoAsync(
                TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);

            await WaitAsync(
                () => Task.FromResult(registry.TryGet(sourceKey, out var src) && src is { Video.Width: > 0 }),
                TimeSpan.FromSeconds(15),
                cts.Token);

            // 关键断言：没人点播时按需代理不拉流
            Assert.False(manager.IsPulling(pullKey), "没人观看时不应拉流");
            Assert.False(registry.TryGet(pullKey, out _), "没人观看时本地不应有这路流");

            // 播放端点播 → 触发按需拉流
            var player = await RtmpPlayClient.ConnectAsync("127.0.0.1", rtmpPort, "live", cts.Token);
            try
            {
                await player.PlayAsync("od", cts.Token);
                var code = string.Empty;
                while (code != "NetStream.Play.Start")
                {
                    (code, _) = await player.ReadNextStatusAsync(cts.Token);
                }

                Assert.True(manager.IsPulling(pullKey), "有人观看时应拉起代理");
                Assert.True(
                    await WaitAsync(
                        () => Task.FromResult(registry.TryGet(pullKey, out var pulled) && pulled is { Video.Width: > 0 }),
                        TimeSpan.FromSeconds(15),
                        cts.Token),
                    "按需代理应从源站拿到视频参数");
            }
            finally
            {
                // 观众离开
                await player.DisposeAsync();
            }

            // 无人观看：idle(1s) + 扫描(1s) 后自动停止，回到待触发状态
            Assert.True(
                await WaitAsync(
                    () => Task.FromResult(!manager.IsPulling(pullKey)),
                    TimeSpan.FromSeconds(15),
                    cts.Token),
                "观众走光后按需代理应自动停止");
            Assert.False(registry.TryGet(pullKey, out _), "停止后本地流应被摘掉");

            // 再点播一次：能重新拉起（按需不是一次性的）
            var player2 = await RtmpPlayClient.ConnectAsync("127.0.0.1", rtmpPort, "live", cts.Token);
            try
            {
                await player2.PlayAsync("od", cts.Token);
                var code2 = string.Empty;
                while (code2 != "NetStream.Play.Start")
                {
                    (code2, _) = await player2.ReadNextStatusAsync(cts.Token);
                }

                Assert.True(manager.IsPulling(pullKey), "第二次点播应重新拉起代理");
            }
            finally
            {
                await player2.DisposeAsync();
            }
        }
        finally
        {
            await publisher.DisposeAsync();
        }
    }

    [Fact]
    public async Task StreamLoss_KeepsReconnectingWhileSomeoneWatches()
    {
        var rtmpPort = TestHost.GetFreePort();
        var pullKey = StreamKey.Create(null, "live", "od-live");
        var origin = $"rtmp://127.0.0.1:{rtmpPort}/live/od-live-src";

        await using var host = await TestHost.StartAsync(rtmpPort, options =>
        {
            options.General.MaxStreamWaitMS = 15000;
            options.General.WaitTrackReadyMS = 15000;
            options.Proxy.RetryDelayMS = 200;
            options.Proxy.OnDemandList = $"live/od-live={origin}";
            options.Proxy.OnDemandIdleMS = 1000;
            options.Proxy.OnDemandMaxWaitMS = 20000;
        });

        using var cts = new CancellationTokenSource(Timeout);
        var manager = host.Services.GetRequiredService<RtmpPullProxyManager>();

        var publisher = await RtmpPublishClient.ConnectAsync("127.0.0.1", rtmpPort, "live", "od-live-src", cts.Token);
        await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
        await publisher.SendVideoAsync(
            TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);

        var player = await RtmpPlayClient.ConnectAsync("127.0.0.1", rtmpPort, "live", cts.Token);
        try
        {
            await player.PlayAsync("od-live", cts.Token);
            var code = string.Empty;
            while (code != "NetStream.Play.Start")
            {
                (code, _) = await player.ReadNextStatusAsync(cts.Token);
            }

            Assert.True(manager.IsPulling(pullKey));

            // 源站断开：只要还有观众，代理必须一直重连而不是停掉
            await publisher.DisposeAsync();

            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(500, cts.Token);
                Assert.True(manager.IsPulling(pullKey), $"断流第 {i + 1} 次检查：有观众时不应停止拉流");
            }
        }
        finally
        {
            await player.DisposeAsync();
        }

        // 观众离开后才停
        Assert.True(
            await WaitAsync(() => Task.FromResult(!manager.IsPulling(pullKey)), TimeSpan.FromSeconds(15), cts.Token),
            "观众走光后应停止重连");
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return await condition().ConfigureAwait(false);
    }
}
