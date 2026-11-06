using System.Net.Http;
using System.Text.Json;
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

    [Fact]
    public async Task Api_RegistersOnDemandProxy_TriggersOnPlay_AndCanBeDeleted()
    {
        var rtmpPort = TestHost.GetFreePort();
        var pullKey = StreamKey.Create(null, "live", "cam-api");
        var sourceKey = StreamKey.Create(null, "live", "cam-src");
        var origin = $"rtmp://127.0.0.1:{rtmpPort}/live/cam-src";

        await using var host = await TestHost.StartAsync(rtmpPort, options =>
        {
            options.General.MaxStreamWaitMS = 15000;
            options.General.WaitTrackReadyMS = 15000;
            options.Proxy.RetryDelayMS = 200;
        });

        using var cts = new CancellationTokenSource(Timeout);
        using var client = new HttpClient();
        var registry = host.Services.GetRequiredService<MediaSourceRegistry>();
        var manager = host.Services.GetRequiredService<RtmpPullProxyManager>();

        var addUrl = $"{host.Address}/index/api/addStreamProxy?app=live&stream=cam-api&on_demand=1" +
                     $"&url={Uri.EscapeDataString(origin)}";

        // 1) 编程登记：只登记，不立刻拉流
        using (var add = JsonDocument.Parse(await client.GetStringAsync(addUrl, cts.Token)))
        {
            Assert.Equal(0, add.RootElement.GetProperty("code").GetInt32());
            Assert.Equal(pullKey.ToString(), add.RootElement.GetProperty("data").GetProperty("key").GetString());
        }

        Assert.False(manager.IsPulling(pullKey), "登记后不应立即拉流");
        Assert.False(registry.TryGet(pullKey, out _), "登记后本地不应有这路流");

        // 2) getProxyList 里能看到"按需待触发"
        var items = await GetProxyListAsync(client, host.Address, cts.Token);
        var item = Assert.Single(items);
        Assert.True(item.GetProperty("onDemand").GetBoolean());
        Assert.False(item.GetProperty("online").GetBoolean());
        Assert.Equal(origin, item.GetProperty("url").GetString());

        // 3) 非法地址拒绝
        using (var bad = JsonDocument.Parse(await client.GetStringAsync(
            $"{host.Address}/index/api/addStreamProxy?app=live&stream=bad&on_demand=1&url=http%3A%2F%2Fexample.com",
            cts.Token)))
        {
            Assert.Equal(-1, bad.RootElement.GetProperty("code").GetInt32());
        }

        // 4) 源站上线 + 播放端点播 → 触发拉流
        var publisher = await RtmpPublishClient.ConnectAsync("127.0.0.1", rtmpPort, "live", "cam-src", cts.Token);
        try
        {
            await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
            await publisher.SendVideoAsync(
                TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);

            await WaitAsync(
                () => Task.FromResult(registry.TryGet(sourceKey, out var src) && src is { Video.Width: > 0 }),
                TimeSpan.FromSeconds(15),
                cts.Token);

            var player = await RtmpPlayClient.ConnectAsync("127.0.0.1", rtmpPort, "live", cts.Token);
            try
            {
                await player.PlayAsync("cam-api", cts.Token);
                var code = string.Empty;
                while (code != "NetStream.Play.Start")
                {
                    (code, _) = await player.ReadNextStatusAsync(cts.Token);
                }

                Assert.True(manager.IsPulling(pullKey), "点播后应按登记信息拉起拉流");
            }
            finally
            {
                await player.DisposeAsync();
            }
        }
        finally
        {
            await publisher.DisposeAsync();
        }

        // 5) delStreamProxy 同时清掉按需登记
        using (var del = JsonDocument.Parse(await client.GetStringAsync(
            $"{host.Address}/index/api/delStreamProxy?key={Uri.EscapeDataString(pullKey.ToString())}", cts.Token)))
        {
            Assert.True(del.RootElement.GetProperty("data").GetProperty("flag").GetBoolean());
        }

        Assert.False(manager.IsPulling(pullKey));
        Assert.Empty(await GetProxyListAsync(client, host.Address, cts.Token));
    }

    private static async Task<JsonElement[]> GetProxyListAsync(HttpClient client, string address, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync($"{address}/index/api/getProxyList", cancellationToken));
        return document.RootElement
            .GetProperty("data")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
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
