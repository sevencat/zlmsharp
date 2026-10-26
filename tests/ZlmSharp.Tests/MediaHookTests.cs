using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Server.Hooks;

namespace ZlmSharp.Tests;

/// <summary>hook 鉴权：请求体与 ZLM 对齐、code!=0/HTTP 错误/超时三类拒绝、fail_open 开关。</summary>
public class MediaHookTests
{
    private static readonly StreamKey Key = StreamKey.Create("__defaultVhost__", "live", "test");

    [Fact]
    public async Task Invoke_SendsZlmCompatiblePayload_AndAllowsOnZero()
    {
        await using var server = await FakeHookServer.StartAsync();
        var service = server.CreateService(server.CreateOptions());

        var result = await service.InvokeAsync(Context(), CancellationToken.None);

        Assert.True(result.Allow);
        Assert.Equal(1, server.Calls);

        var body = server.LastBody ?? string.Empty;
        Assert.Contains("\"schema\":\"rtmp\"", body);
        Assert.Contains("\"app\":\"live\"", body);
        Assert.Contains("\"stream\":\"test\"", body);
        Assert.Contains("\"vhost\":\"__defaultVhost__\"", body);
        Assert.Contains("\"id\":\"session-1\"", body);
        Assert.Contains("\"ip\":\"127.0.0.1\"", body);
        Assert.Contains("\"port\":1234", body);
        Assert.Contains("\"user_agent\":\"test-agent\"", body);
        Assert.Contains("\"params\":\"token=abc\"", body);
        Assert.Contains("\"type\":\"publish\"", body);
        Assert.Contains("\"mediaServerId\":\"your_server_id\"", body);
    }

    [Fact]
    public async Task Invoke_DeniesOnNonZeroCode()
    {
        await using var server = await FakeHookServer.StartAsync();
        server.Handler = _ => Task.FromResult((200, "{\"code\":-1,\"msg\":\"bad token\"}"));
        var service = server.CreateService(server.CreateOptions());

        var result = await service.InvokeAsync(Context(), CancellationToken.None);

        Assert.False(result.Allow);
        Assert.Equal(-1, result.Code);
        Assert.Equal("bad token", result.Message);
    }

    [Fact]
    public async Task Invoke_DeniesOnHttpError_ByDefault()
    {
        await using var server = await FakeHookServer.StartAsync();
        server.Handler = _ => Task.FromResult((500, "boom"));
        var service = server.CreateService(server.CreateOptions());

        var result = await service.InvokeAsync(Context(), CancellationToken.None);

        Assert.False(result.Allow);
        Assert.Equal(500, result.Code);
        Assert.Contains("500", result.Message ?? string.Empty);
    }

    [Fact]
    public async Task Invoke_AllowsOnHttpError_WhenFailOpen()
    {
        await using var server = await FakeHookServer.StartAsync();
        server.Handler = _ => Task.FromResult((503, "down"));
        var service = server.CreateService(server.CreateOptions(failOpen: true));

        var result = await service.InvokeAsync(Context(), CancellationToken.None);

        Assert.True(result.Allow);
    }

    [Fact]
    public async Task Invoke_DeniesOnTimeout()
    {
        await using var server = await FakeHookServer.StartAsync();
        server.Handler = async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            return (200, "{\"code\":0}");
        };
        var service = server.CreateService(server.CreateOptions(timeoutSec: 1));

        var result = await service.InvokeAsync(Context(), CancellationToken.None);

        Assert.False(result.Allow);
        Assert.Contains("hook", result.Message ?? string.Empty);
    }

    [Fact]
    public async Task Invoke_SkipsRequestWhenEndpointNotConfigured()
    {
        var options = new AppOptions();
        options.Hook.Enable = true; // 但没配地址
        var service = new MediaHookService(options, NullLoggerFactory.Instance);

        var result = await service.InvokeAsync(Context(), CancellationToken.None);

        Assert.True(result.Allow);
    }

    [Fact]
    public async Task ShouldCloseNoneReader_FollowsHookCode()
    {
        await using var server = await FakeHookServer.StartAsync();
        server.Handler = _ => Task.FromResult((200, "{\"code\":1,\"msg\":\"keep\"}"));

        var options = server.CreateOptions();
        options.Hook.OnStreamNoneReader = server.Url;
        var service = server.CreateService(options);

        Assert.False(await service.ShouldCloseNoneReaderAsync(Key, "rtmp", "id", CancellationToken.None));

        server.Handler = _ => Task.FromResult((200, "{\"code\":0}"));
        Assert.True(await service.ShouldCloseNoneReaderAsync(Key, "rtmp", "id", CancellationToken.None));
    }

    [Fact]
    public async Task RtmpPublish_IsRejectedWhenHookDenies()
    {
        await using var server = await FakeHookServer.StartAsync();
        server.Handler = _ => Task.FromResult((200, "{\"code\":1,\"msg\":\"denied\"}"));

        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort, options =>
        {
            options.Hook.Enable = true;
            options.Hook.OnPublish = server.Url;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var publisher = await host.ConnectPublisherAsync(rtmpPort, cts.Token, "NetStream.Publish.Failed");
        await publisher.DisposeAsync();

        Assert.Equal(1, server.Calls);
        var registry = host.Services.GetRequiredService<MediaSourceRegistry>();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task RtmpPublish_SucceedsWhenHookAllows()
    {
        await using var server = await FakeHookServer.StartAsync();

        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort, options =>
        {
            options.Hook.Enable = true;
            options.Hook.OnPublish = server.Url;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var publisher = await host.ConnectPublisherAsync(rtmpPort, cts.Token);
        await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
        await publisher.SendVideoAsync(TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);

        Assert.Equal(1, server.Calls);
        var registry = host.Services.GetRequiredService<MediaSourceRegistry>();
        Assert.Equal(1, registry.Count);
    }

    private static MediaHookContext Context() => new(
        "rtmp",
        MediaHookType.Publish,
        Key,
        "session-1",
        "127.0.0.1",
        1234,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["token"] = "abc" },
        "test-agent");
}

/// <summary>测试用的 hook 服务：记录请求体，按 <see cref="Handler"/> 返回响应。</summary>
internal sealed class FakeHookServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeHookServer(WebApplication app) => _app = app;

    public string Url { get; private set; } = string.Empty;

    public string? LastBody { get; private set; }

    public int Calls { get; private set; }

    public Func<string, Task<(int Status, string Body)>> Handler { get; set; } =
        _ => Task.FromResult((200, "{\"code\":0,\"msg\":\"success\"}"));

    public static async Task<FakeHookServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var server = new FakeHookServer(app);

        app.MapPost("/hook", async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            server.LastBody = await reader.ReadToEndAsync();
            server.Calls++;

            var (status, body) = await server.Handler(server.LastBody);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(body);
        });

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses;

        server.Url = addresses.First().Replace("[::]", "127.0.0.1").TrimEnd('/') + "/hook";
        return server;
    }

    public AppOptions CreateOptions(bool failOpen = false, int timeoutSec = 5)
    {
        var options = new AppOptions();
        options.Hook.Enable = true;
        options.Hook.OnPublish = Url;
        options.Hook.OnPlay = Url;
        options.Hook.TimeoutSec = timeoutSec;
        options.Hook.FailOpen = failOpen;
        return options;
    }

    public MediaHookService CreateService(AppOptions options) => new(options, NullLoggerFactory.Instance);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
