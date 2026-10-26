using System.Net;
using System.Net.Sockets;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Tests;

public class RtmpSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Publish_RegistersSourceAndIngestsAvcFrames()
    {
        var registry = new MediaSourceRegistry();
        var (listener, serverTask, port) = StartSessionServer(registry);
        var key = StreamKey.Create(null, "live", "test");

        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var publisher = await RtmpPublishClient.ConnectAsync("127.0.0.1", port, "live", "test", cts.Token);

            try
            {
                await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
                await publisher.SendVideoAsync(
                    TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);
                await publisher.SendVideoAsync(
                    TestData.AnnexBInterFrame(), keyFrame: false, timestamp: 40, cancellationToken: cts.Token);

                var source = await WaitForSourceAsync(registry, key);
                Assert.NotNull(source);

                Assert.Equal(CodecId.H264, source!.Video.CodecId);
                Assert.Equal(TestData.Sps720, source.Video.Sps);
                Assert.Equal(TestData.Pps, source.Video.Pps);
                Assert.True(source.Video.Width > 0, "SPS 解析出的宽度应大于 0");

                using var frames = new FrameBag(source.SnapshotGop());
                var list = frames.Frames;
                Assert.Equal(3, list.Count);

                Assert.True(list[0].IsConfig);
                Assert.Equal(TestData.AnnexBConfig(), list[0].Data.ToArray());
                Assert.Equal(0, list[0].Dts);

                Assert.True(list[1].IsKeyFrame);
                Assert.Equal(TestData.AnnexBKeyFrame(), list[1].Data.ToArray());

                Assert.False(list[2].IsKeyFrame);
                Assert.Equal(TestData.AnnexBInterFrame(), list[2].Data.ToArray());
                Assert.Equal(40, list[2].Dts);
            }
            finally
            {
                await publisher.DisposeAsync();
            }

            await serverTask.WaitAsync(Timeout);
            Assert.False(registry.TryGet(key, out _), "推流断开后应移除流");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Publish_ReadsVhostFromStreamNameQuery()
    {
        var registry = new MediaSourceRegistry();
        var (listener, serverTask, port) = StartSessionServer(registry);
        var key = StreamKey.Create("v1", "live", "camera");

        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var publisher = await RtmpPublishClient.ConnectAsync(
                "127.0.0.1", port, "live", "camera?vhost=v1", cts.Token);

            try
            {
                var source = await WaitForSourceAsync(registry, key, minimumFrames: 0);
                Assert.NotNull(source);
                Assert.Equal("v1", source!.Key.Vhost);
                Assert.Equal("camera", source.Key.Stream);
            }
            finally
            {
                await publisher.DisposeAsync();
            }

            await serverTask.WaitAsync(Timeout);
            Assert.False(registry.TryGet(key, out _));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static (TcpListener Listener, Task ServerTask, int Port) StartSessionServer(MediaSourceRegistry registry)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            await using var stream = accepted.GetStream();
            var session = new RtmpSession(
                stream,
                registry,
                new RtmpSessionOptions { HandshakeTimeout = Timeout });
            await session.RunAsync(CancellationToken.None);
        });

        return (listener, serverTask, port);
    }

    private static async Task<MediaSource?> WaitForSourceAsync(
        MediaSourceRegistry registry,
        StreamKey key,
        int minimumFrames = 3)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (registry.TryGet(key, out var source) &&
                source is { IsClosed: false } &&
                source.FrameCacheCount >= minimumFrames)
            {
                return source;
            }

            await Task.Delay(20);
        }

        return null;
    }

    /// <summary>GOP 快照里的帧需要由调用方 Dispose。</summary>
    private sealed class FrameBag : IDisposable
    {
        public FrameBag(IReadOnlyList<MediaFrame> frames)
        {
            Frames = frames;
        }

        public IReadOnlyList<MediaFrame> Frames { get; }

        public void Dispose()
        {
            foreach (var frame in Frames)
            {
                frame.Dispose();
            }
        }
    }
}
