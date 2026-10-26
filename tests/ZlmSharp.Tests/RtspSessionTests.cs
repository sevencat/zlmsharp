using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtsp;

namespace ZlmSharp.Tests;

public class RtspSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Play_DescribeSetupThenStreamsRtp()
    {
        var registry = new MediaSourceRegistry();
        var key = StreamKey.Create(null, "live", "test");
        var source = registry.GetOrCreate(key, "rtsp");
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBConfig(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Config | FrameFlags.Key));
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBKeyFrame(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Key));

        var (listener, serverTask, port) = StartSessionServer(registry);

        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var client = await RtspTestClient.ConnectAsync("127.0.0.1", port, cts.Token);
            var baseUri = $"rtsp://127.0.0.1:{port}/live/test";
            var trackUri = baseUri + "/trackID=0";

            try
            {
                await client.SendAsync("OPTIONS", baseUri, cts.Token);
                var options = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, options.StatusCode);
                Assert.Contains("DESCRIBE", options.Header("Public")!);

                await client.SendAsync("DESCRIBE", baseUri, cts.Token, ("Accept", "application/sdp"));
                var describe = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, describe.StatusCode);
                Assert.Equal("application/sdp", describe.Header("Content-Type"));
                Assert.Contains("a=rtpmap:96 H264/90000", describe.Body);
                Assert.Contains("sprop-parameter-sets=", describe.Body);
                Assert.Contains("a=control:trackID=0", describe.Body);

                await client.SendAsync(
                    "SETUP",
                    trackUri,
                    cts.Token,
                    ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
                var setup = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, setup.StatusCode);
                Assert.Contains("interleaved=0-1", setup.Header("Transport")!);
                Assert.NotNull(setup.Header("Session"));

                await client.SendAsync("PLAY", trackUri, cts.Token, ("Range", "npt=0.000-"));
                var play = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, play.StatusCode);
                Assert.Contains("trackID=0", play.Header("RTP-Info")!);

                // GOP 里的关键帧：单 NALU，ts=0（通道 1 的 RTCP SR 会被跳过）
                var packet = await client.ReadInterleavedPayloadAsync(0, cts.Token);
                Assert.Equal(0x80, packet[0]);
                Assert.Equal(96, packet[1] & 0x7F);
                Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
                Assert.Equal(0x65, packet[12]);

                // 推流端再写一帧，应实时收到（dts 40ms → 90kHz 时间戳 3600）
                source.AddFrame(MediaFrame.Create(
                    TestData.AnnexBInterFrame(), TrackType.Video, CodecId.H264, 40, 40));

                var nextPacket = await client.ReadInterleavedPayloadAsync(0, cts.Token);
                Assert.Equal(3600u, BinaryPrimitives.ReadUInt32BigEndian(nextPacket.AsSpan(4)));
                Assert.Equal(0x41, nextPacket[12]);

                await client.SendAsync("TEARDOWN", trackUri, cts.Token);
                var teardown = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, teardown.StatusCode);
            }
            finally
            {
                await client.DisposeAsync();
            }

            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, source.ReaderCount);
        }
        finally
        {
            listener.Stop();
            source.Close();
        }
    }

    [Fact]
    public async Task Setup_UnsupportedTransport_Returns461()
    {
        var registry = new MediaSourceRegistry();
        var (listener, serverTask, port) = StartSessionServer(registry);

        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var client = await RtspTestClient.ConnectAsync("127.0.0.1", port, cts.Token);
            var trackUri = $"rtsp://127.0.0.1:{port}/live/test/trackID=0";

            try
            {
                await client.SendAsync(
                    "SETUP",
                    trackUri,
                    cts.Token,
                    ("Transport", "RAW/RAW/UDP;unicast;client_port=8000-8001"));

                var setup = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(461, setup.StatusCode);
                Assert.Equal("Unsupported Transport", setup.Reason);
            }
            finally
            {
                await client.DisposeAsync();
            }

            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Setup_UdpTransport_StreamsRtpAndRtcpSenderReport()
    {
        var registry = new MediaSourceRegistry();
        var key = StreamKey.Create(null, "live", "test");
        var source = registry.GetOrCreate(key, "rtsp");
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBConfig(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Config | FrameFlags.Key));
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBKeyFrame(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Key));

        var (listener, serverTask, port) = StartSessionServer(registry);
        using var rtpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var rtcpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var rtpPort = ((IPEndPoint)rtpClient.Client.LocalEndPoint!).Port;
        var rtcpPort = ((IPEndPoint)rtcpClient.Client.LocalEndPoint!).Port;

        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var client = await RtspTestClient.ConnectAsync("127.0.0.1", port, cts.Token);
            var baseUri = $"rtsp://127.0.0.1:{port}/live/test";
            var trackUri = baseUri + "/trackID=0";

            try
            {
                await client.SendAsync(
                    "SETUP",
                    trackUri,
                    cts.Token,
                    ("Transport", $"RTP/AVP;unicast;client_port={rtpPort}-{rtcpPort}"));
                var setup = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, setup.StatusCode);

                var responseTransport = setup.Header("Transport")!;
                Assert.StartsWith("RTP/AVP;", responseTransport);
                Assert.Contains($"client_port={rtpPort}-{rtcpPort}", responseTransport);
                Assert.Contains("server_port=", responseTransport);

                await client.SendAsync("PLAY", trackUri, cts.Token, ("Range", "npt=0.000-"));
                var play = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, play.StatusCode);

                var rtpPacket = (await rtpClient.ReceiveAsync(cts.Token)).Buffer;
                Assert.Equal(0x80, rtpPacket[0]);
                Assert.Equal(96, rtpPacket[1] & 0x7F);
                Assert.Equal(0x65, rtpPacket[12]);
                var ssrc = BinaryPrimitives.ReadUInt32BigEndian(rtpPacket.AsSpan(8));
                Assert.Contains($"ssrc={ssrc:x8}", responseTransport);

                // 首帧之后立刻发一次 SR，便于客户端建立同步源
                var senderReport = (await rtcpClient.ReceiveAsync(cts.Token)).Buffer;
                Assert.Equal(0x80, senderReport[0]);
                Assert.Equal(200, senderReport[1]);
                Assert.Equal(6, BinaryPrimitives.ReadUInt16BigEndian(senderReport.AsSpan(2)));
                Assert.Equal(ssrc, BinaryPrimitives.ReadUInt32BigEndian(senderReport.AsSpan(4)));
                Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(senderReport.AsSpan(20)));
                Assert.Equal(
                    (uint)rtpPacket.Length - 12,
                    BinaryPrimitives.ReadUInt32BigEndian(senderReport.AsSpan(24)));

                // SR 的 NTP 时间与 RTP 时间戳都要落在合理范围（2026 年、首帧附近）
                var ntpSeconds = BinaryPrimitives.ReadUInt64BigEndian(senderReport.AsSpan(8)) >> 32;
                Assert.InRange(ntpSeconds, 3_900_000_000UL, 4_100_000_000UL);
                Assert.InRange(BinaryPrimitives.ReadUInt32BigEndian(senderReport.AsSpan(16)), 0u, 90_000u);

                await client.SendAsync("TEARDOWN", trackUri, cts.Token);
                var teardown = await client.ReadResponseAsync(cts.Token);
                Assert.Equal(200, teardown.StatusCode);
            }
            finally
            {
                await client.DisposeAsync();
            }

            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, source.ReaderCount);
        }
        finally
        {
            listener.Stop();
            source.Close();
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
            var session = new RtspSession(
                stream,
                registry,
                new RtspSessionOptions
                {
                    HandshakeTimeout = Timeout,
                    MaxStreamWaitMS = 2000,
                    WaitTrackReadyMS = 2000,
                },
                null,
                (IPEndPoint)accepted.Client.RemoteEndPoint!);
            await session.RunAsync(CancellationToken.None);
        });

        return (listener, serverTask, port);
    }
}
