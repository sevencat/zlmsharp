using System.Buffers;
using System.Net;
using System.Net.Sockets;
using ZlmSharp.Codecs;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Tests;

public class RtmpPlaybackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Play_SendsStatusMetadataSequenceHeaderAndFrames()
    {
        var registry = new MediaSourceRegistry();
        var key = StreamKey.Create(null, "live", "test");
        var source = registry.GetOrCreate(key, "rtmp");
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBConfig(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Config | FrameFlags.Key));
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBKeyFrame(), TrackType.Video, CodecId.H264, 1000, 1000, FrameFlags.Key));

        var (listener, serverTask, port) = StartSessionServer(registry);

        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var client = await RtmpPlayClient.ConnectAsync("127.0.0.1", port, "live", cts.Token);

            try
            {
                await client.PlayAsync("test", cts.Token);

                var codes = new List<string>();
                string? lastLevel = null;
                while (!codes.Contains("NetStream.Play.Start"))
                {
                    var (code, level) = await client.ReadNextStatusAsync(cts.Token);
                    codes.Add(code);
                    lastLevel = level;
                }

                Assert.Contains("NetStream.Play.Reset", codes);
                Assert.Equal("status", lastLevel);

                var metadata = await client.ReadMessageAsync(cts.Token);
                Assert.NotNull(metadata);
                Assert.Equal(RtmpMessageType.DataAmf0, metadata!.Type);
                Assert.True(Amf0.TryDecode(metadata.Payload, out var metadataValues, out var metadataError),
                    metadataError);
                Assert.Equal("onMetaData", Assert.IsType<Amf0String>(metadataValues[0]).Value);
                Assert.True(metadataValues[1].GetNumberProperty("width") > 0, "onMetaData 应带宽度");

                var configMessage = await client.ReadMessageAsync(cts.Token);
                Assert.NotNull(configMessage);
                Assert.Equal(RtmpMessageType.Video, configMessage!.Type);
                Assert.Equal(0, configMessage.Payload[1]); // AVC sequence header
                Assert.True(
                    H264Avcc.TryParseConfigRecord(configMessage.Payload.AsSpan(5), out var record, out var recordError),
                    recordError);
                Assert.Equal(TestData.Sps720, record!.Sps);
                Assert.Equal(TestData.Pps, record.Pps);

                var keyMessage = await client.ReadMessageAsync(cts.Token);
                Assert.NotNull(keyMessage);
                Assert.Equal(RtmpMessageType.Video, keyMessage!.Type);
                Assert.Equal(0x17, keyMessage.Payload[0]);
                Assert.Equal(1, keyMessage.Payload[1]);
                Assert.Equal(0u, keyMessage.Timestamp);
                Assert.Equal(BuildAvcc(TestData.AnnexBKeyFrame()), keyMessage.Payload[5..]);

                // 推流端再写一帧，应实时转发
                source.AddFrame(MediaFrame.Create(
                    TestData.AnnexBInterFrame(), TrackType.Video, CodecId.H264, 1040, 1040));

                var interMessage = await client.ReadMessageAsync(cts.Token);
                Assert.NotNull(interMessage);
                Assert.Equal(RtmpMessageType.Video, interMessage!.Type);
                Assert.Equal(0x27, interMessage.Payload[0]);
                Assert.Equal(1, interMessage.Payload[1]);
                Assert.Equal(40u, interMessage.Timestamp);
                Assert.Equal(BuildAvcc(TestData.AnnexBInterFrame()), interMessage.Payload[5..]);
            }
            finally
            {
                await client.DisposeAsync();
            }

            await serverTask.WaitAsync(Timeout);
            Assert.Equal(0, source.ReaderCount);
        }
        finally
        {
            listener.Stop();
            source.Close();
        }
    }

    [Fact]
    public async Task Play_UnknownStream_ReportsStreamNotFound()
    {
        var registry = new MediaSourceRegistry();
        var (listener, serverTask, port) = StartSessionServer(registry, maxStreamWaitMs: 0);

        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var client = await RtmpPlayClient.ConnectAsync("127.0.0.1", port, "live", cts.Token);

            try
            {
                await client.PlayAsync("missing", cts.Token);

                var (code, level) = await client.ReadNextStatusAsync(cts.Token);
                Assert.Equal("NetStream.Play.StreamNotFound", code);
                Assert.Equal("error", level);
            }
            finally
            {
                await client.DisposeAsync();
            }

            await serverTask.WaitAsync(Timeout);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static (TcpListener Listener, Task ServerTask, int Port) StartSessionServer(
        MediaSourceRegistry registry,
        int maxStreamWaitMs = 5000)
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
                new RtmpSessionOptions
                {
                    HandshakeTimeout = Timeout,
                    MaxStreamWaitMS = maxStreamWaitMs,
                    WaitTrackReadyMS = 2000,
                });
            await session.RunAsync(CancellationToken.None);
        });

        return (listener, serverTask, port);
    }

    private static byte[] BuildAvcc(byte[] annexB)
    {
        var buffer = new ArrayBufferWriter<byte>(annexB.Length + 8);
        var count = H264Avcc.ToAvcc(
            AnnexB.Split(annexB).Select(nal => new ReadOnlyMemory<byte>(nal)),
            4,
            buffer);
        Assert.True(count > 0);
        return buffer.WrittenSpan.ToArray();
    }
}
