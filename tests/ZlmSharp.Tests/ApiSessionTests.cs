using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ZlmSharp.Tests;

/// <summary>会话列表与 kick_session：注册表要能列出推流端，并在踢人后清空。</summary>
public class ApiSessionTests
{
    [Fact]
    public async Task GetSessionList_ShowsPublisher_AndKickSessionDisconnectsIt()
    {
        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var publisher = await host.ConnectPublisherAsync(rtmpPort, cts.Token);

        try
        {
            await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
            await publisher.SendVideoAsync(
                TestData.AnnexBKeyFrame(),
                keyFrame: true,
                timestamp: 0,
                cancellationToken: cts.Token);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var listUrl = $"{host.Address}/index/api/getSessionList?type=publish";

            Assert.True(
                await WaitAsync(
                    () => Task.FromResult(
                        host.Services.GetRequiredService<ZlmSharp.Core.MediaSessionRegistry>().Count == 1),
                    TimeSpan.FromSeconds(5),
                    cts.Token),
                "推流会话没有注册到 MediaSessionRegistry");

            Assert.True(
                await WaitAsync(
                    async () => (await client.GetStringAsync(listUrl, cts.Token)).Contains("\"id\":", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5),
                    cts.Token),
                "getSessionList 没有列出推流会话");

            var json = await client.GetStringAsync(listUrl, cts.Token);
            Assert.Contains("\"type\":\"publish\"", json);
            Assert.Contains("\"schema\":\"rtmp\"", json);
            Assert.Contains("\"app\":\"live\"", json);
            Assert.Contains("\"stream\":\"test\"", json);

            var kickJson = await client.GetStringAsync(
                $"{host.Address}/index/api/kick_session?type=publish",
                cts.Token);
            Assert.Contains("\"count\":1", kickJson);

            Assert.True(
                await WaitAsync(
                    async () => (await client.GetStringAsync(listUrl, cts.Token)).Contains("\"data\":[]", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5),
                    cts.Token),
                "kick_session 之后会话仍然存在");
        }
        finally
        {
            await publisher.DisposeAsync();
        }
    }

    [Fact]
    public async Task KickSession_WithUnknownId_KicksNothing()
    {
        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var json = await client.GetStringAsync(
            $"{host.Address}/index/api/kick_session?id=not-exists",
            CancellationToken.None);

        Assert.Contains("\"count\":0", json);
    }

    [Fact]
    public async Task KickSession_WithoutAnyFilter_ReturnsError()
    {
        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var json = await client.GetStringAsync(
            $"{host.Address}/index/api/kick_session",
            CancellationToken.None);

        Assert.Contains("\"code\":-1", json);
    }

    private static async Task<bool> WaitAsync(
        Func<Task<bool>> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await probe())
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(100, cancellationToken);
        }

        return false;
    }
}
