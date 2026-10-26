using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Flv;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>端到端：同一路径的 WebSocket 升级走 WS-FLV，输出与 HTTP-FLV 相同的 FLV 字节流。</summary>
public class WebSocketFlvIntegrationTests
{
    [Fact]
    public async Task WebSocketFlv_StreamsHeaderMetadataAndTags()
    {
        var app = await StartHostAsync();

        try
        {
            var registry = app.Services.GetRequiredService<MediaSourceRegistry>();
            var source = registry.GetOrCreate(StreamKey.Create(null, "live", "test"), "live");
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBConfig(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Config | FrameFlags.Key));
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBKeyFrame(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Key));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var socket = new ClientWebSocket();
            var address = GetAddress(app).Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase);
            await socket.ConnectAsync(new Uri($"{address}/live/test.live.flv"), cts.Token);

            var collected = new List<byte>();
            var buffer = new byte[8192];

            var tags = await ReadTagsAsync(socket, collected, buffer, minimumTags: 3, cts.Token);
            Assert.Equal(3, tags.Count);
            Assert.Equal(FlvTagType.Script, tags[0].Type);
            Assert.Equal(FlvTagType.Video, tags[1].Type);
            Assert.Equal(0, tags[1].AvcPacketType);
            Assert.True(tags[2].IsKeyFrame);
            Assert.Equal(0u, tags[2].Timestamp);

            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBInterFrame(), TrackType.Video, CodecId.H264, 40, 40));

            tags = await ReadTagsAsync(socket, collected, buffer, minimumTags: 4, cts.Token);
            Assert.True(tags.Count >= 4);
            Assert.Equal(40u, tags[3].Timestamp);
            Assert.False(tags[3].IsKeyFrame);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<List<FlvTag>> ReadTagsAsync(
        ClientWebSocket socket,
        List<byte> collected,
        byte[] buffer,
        int minimumTags,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var tags = FlvTagParser.ParseAll(collected.ToArray());
            if (tags.Count >= minimumTags)
            {
                return tags;
            }

            using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            receiveCts.CancelAfter(TimeSpan.FromSeconds(2));

            byte[] message;
            try
            {
                message = await ReceiveMessageAsync(socket, buffer, receiveCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (message.Length == 0)
            {
                break;
            }

            collected.AddRange(message);
        }

        return FlvTagParser.ParseAll(collected.ToArray());
    }

    private static async Task<byte[]> ReceiveMessageAsync(
        ClientWebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return [];
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return stream.ToArray();
    }

    private static async Task<WebApplication> StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var options = new AppOptions();
        options.Http.AllowOrigins = "*";
        options.General.MaxStreamWaitMS = 2000;
        options.General.WaitTrackReadyMS = 2000;

        builder.Services.AddZlmSharp(o =>
        {
            o.Options = options;
            o.ConfigureLogging = false;
            o.RegisterStreamListeners = false;
        });

        var app = builder.Build();
        app.UseZlmSharp();
        await app.StartAsync();
        return app;
    }

    private static string GetAddress(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses;

        return addresses.First().Replace("[::]", "127.0.0.1").TrimEnd('/');
    }
}
