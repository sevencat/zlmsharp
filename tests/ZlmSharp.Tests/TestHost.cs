using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>起一个带 RTMP 监听 + HTTP API 的最小宿主，供 hook / 会话 API 的端到端测试复用。</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestHost(WebApplication app) => _app = app;

    public IServiceProvider Services => _app.Services;

    public string Address { get; private init; } = string.Empty;

    public static async Task<TestHost> StartAsync(int rtmpPort, Action<AppOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var options = new AppOptions();
        options.Http.AllowOrigins = "*";
        options.General.MaxStreamWaitMS = 2000;
        options.General.WaitTrackReadyMS = 2000;
        options.Rtsp.Port = 0;
        options.Rtmp.Port = rtmpPort;
        configure?.Invoke(options);

        builder.Services.AddZlmSharp(o =>
        {
            o.Options = options;
            o.ConfigureLogging = false;
        });

        var app = builder.Build();
        app.UseZlmSharp();
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses;

        return new TestHost(app)
        {
            Address = addresses.First().Replace("[::]", "127.0.0.1").TrimEnd('/'),
        };
    }

    public async Task<RtmpPublishClient> ConnectPublisherAsync(
        int rtmpPort,
        CancellationToken cancellationToken,
        string expectedPublishCode = "NetStream.Publish.Start")
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RtmpPublishClient.ConnectAsync(
                    "127.0.0.1",
                    rtmpPort,
                    "live",
                    "test",
                    cancellationToken,
                    expectedPublishCode);
            }
            catch (SocketException) when (attempt < 20)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
