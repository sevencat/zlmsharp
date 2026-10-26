using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ZlmSharp.Codecs;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtsp;

namespace ZlmSharp.Tests;

/// <summary>RTSP 推流二期：AAC 音频轨解析/解包、推流侧 RTCP RR。</summary>
public class RtspPushAudioTests
{
    private const string SdpWithAudio = """
        v=0
        o=- 0 0 IN IP4 127.0.0.1
        s=test
        c=IN IP4 127.0.0.1
        t=0 0
        m=video 0 RTP/AVP 96
        a=rtpmap:96 H264/90000
        a=fmtp:96 packetization-mode=1;sprop-parameter-sets={SPS},{PPS}
        a=control:trackID=0
        m=audio 0 RTP/AVP 97
        a=rtpmap:97 MPEG4-GENERIC/48000/2
        a=fmtp:97 profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=1190
        a=control:trackID=1
        """;

    [Fact]
    public void SdpParser_ParsesVideoAndAudio()
    {
        var sdp = BuildSdp();

        Assert.True(SdpParser.TryParse(sdp, out var description, out var error), error);

        Assert.NotNull(description);
        Assert.NotNull(description!.Video);
        Assert.NotNull(description.Audio);

        Assert.Equal(96, description.Video!.PayloadType);
        Assert.Equal(90_000, description.Video.ClockRate);
        Assert.Equal(0, description.Video.TrackId);
        Assert.NotNull(description.Video.Sps);
        Assert.NotNull(description.Video.Pps);

        Assert.Equal(97, description.Audio!.PayloadType);
        Assert.Equal(48_000, description.Audio.ClockRate);
        Assert.Equal(2, description.Audio.Channels);
        Assert.Equal(1, description.Audio.TrackId);
        Assert.Equal(new byte[] { 0x11, 0x90 }, description.Audio.AudioSpecificConfig);
    }

    [Fact]
    public void SdpParser_VideoOnly_StillParsesAsH264()
    {
        var sdp = BuildSdp(withAudio: false);

        Assert.True(SdpParser.TryParseH264(sdp, out var track, out var error), error);
        Assert.NotNull(track);
        Assert.Equal(96, track!.PayloadType);
        Assert.True(track.HasParameterSets);
    }

    [Fact]
    public void SdpParser_RejectsSdpWithoutSupportedTracks()
    {
        Assert.False(SdpParser.TryParse("v=0\r\nm=video 0 RTP/AVP 98\r\na=rtpmap:98 H265/90000\r\n", out _, out var error));
        Assert.Contains("H.264", error);
    }

    [Fact]
    public void AacDepacketizer_ExtractsSingleAu()
    {
        // AU-headers-length = 16 bit；AU header: size=5, index=0
        byte[] payload = [0x00, 0x10, 0x00, 0x05, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        var depacketizer = new AacRtpDepacketizer();

        Assert.True(depacketizer.Push(payload, out var accessUnit));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE }, accessUnit);
        Assert.Equal(0, depacketizer.SkippedAuCount);
    }

    [Fact]
    public void AacDepacketizer_TakesFirstAuWhenPacketCarriesSeveral()
    {
        // 两个 AU：size=2 与 size=3
        byte[] payload = [0x00, 0x20, 0x00, 0x02, 0x00, 0x03, 0x01, 0x02, 0x03, 0x04, 0x05];
        var depacketizer = new AacRtpDepacketizer();

        Assert.True(depacketizer.Push(payload, out var accessUnit));
        Assert.Equal(new byte[] { 0x01, 0x02 }, accessUnit);
        Assert.Equal(1, depacketizer.SkippedAuCount);
    }

    [Fact]
    public void AacDepacketizer_FallsBackWhenNoAuHeader()
    {
        byte[] payload = [0x00, 0x00, 0x11, 0x22];
        var depacketizer = new AacRtpDepacketizer();

        Assert.True(depacketizer.Push(payload, out var accessUnit));
        Assert.Equal(payload, accessUnit);
    }

    [Fact]
    public void ReceiverReport_HasExpectedLayout()
    {
        var report = RtcpReceiverReport.Build(
            ssrc: 0x01020304,
            sourceSsrc: 0x05060708,
            fractionLost: 10,
            cumulativeLost: 3,
            highestSequence: 1234,
            jitter: 7,
            lastSr: 0x0A0B0C0D,
            delaySinceLastSr: 0x1357);

        Assert.Equal(32, report.Length);
        Assert.Equal(0x81, report[0]);
        Assert.Equal(201, report[1]);
        Assert.Equal(7, BinaryPrimitives.ReadUInt16BigEndian(report.AsSpan(2)));
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(4)));
        Assert.Equal(0x05060708u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(8)));
        Assert.Equal(10, report[12]);
        Assert.Equal(3, (report[13] << 16) | (report[14] << 8) | report[15]);
        Assert.Equal(1234u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(16)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(20)));
        Assert.Equal(0x0A0B0C0Du, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(24)));
        Assert.Equal(0x1357u, BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(28)));
    }

    [Fact]
    public void ReceiverReport_ClampsNegativeLossIntoSigned24Bit()
    {
        var report = RtcpReceiverReport.Build(1, 2, 0, -1, 0, 0, 0, 0);

        var lost = (report[13] << 16) | (report[14] << 8) | report[15];
        Assert.Equal(-1, lost | unchecked((int)0xFF000000));
    }

    [Fact]
    public void SenderReportParser_ExtractsSsrcAndNtpMiddle()
    {
        var packet = new byte[28];
        packet[0] = 0x80;
        packet[1] = 200;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 6);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 0xAABBCCDD);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), 0x1122334455667788);

        Assert.True(RtcpPacket.TryParseSenderReport(packet, out var ssrc, out var ntpMiddle));
        Assert.Equal(0xAABBCCDDu, ssrc);
        // LSR 取 NTP 时间戳的中间 32 位（bits 16..47）
        Assert.Equal(0x33445566u, ntpMiddle);
    }

    [Fact]
    public async Task PushSession_WritesAudioTrack_AndSendsReceiverReport()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var registry = new MediaSourceRegistry();

        var client = await RtspTestClient.ConnectAsync("127.0.0.1", port, cts.Token);
        using var accepted = await listener.AcceptTcpClientAsync(cts.Token);
        var serverStream = accepted.GetStream();
        var reader = new RtspMessageReader(serverStream);

        var baseUri = $"rtsp://127.0.0.1:{port}/live/test";
        await client.SendAsync(
            "ANNOUNCE",
            baseUri,
            BuildSdp(),
            cts.Token,
            ("Content-Type", "application/sdp"));

        var first = await reader.ReadNextAsync(cts.Token);
        Assert.NotNull(first.Request);

        var session = new RtspPushSession(
            serverStream,
            reader,
            [first.Request!],
            registry,
            new RtspSessionOptions { RtcpInterval = TimeSpan.FromMilliseconds(300) },
            log: null,
            remoteEndPoint: new IPEndPoint(IPAddress.Loopback, port));

        var runTask = session.RunAsync(cts.Token);

        try
        {
            Assert.Equal(200, (await client.ReadResponseAsync(cts.Token)).StatusCode);

            await client.SendAsync(
                "SETUP",
                baseUri + "/trackID=0",
                cts.Token,
                ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
            var videoSetup = await client.ReadResponseAsync(cts.Token);
            Assert.Equal(200, videoSetup.StatusCode);
            Assert.Contains("interleaved=0-1", videoSetup.Header("Transport") ?? string.Empty);

            await client.SendAsync(
                "SETUP",
                baseUri + "/trackID=1",
                cts.Token,
                ("Transport", "RTP/AVP/TCP;unicast;interleaved=2-3"));
            var audioSetup = await client.ReadResponseAsync(cts.Token);
            Assert.Equal(200, audioSetup.StatusCode);
            Assert.Contains("interleaved=2-3", audioSetup.Header("Transport") ?? string.Empty);

            await client.SendAsync("RECORD", baseUri, cts.Token, ("Session", "test"));
            Assert.Equal(200, (await client.ReadResponseAsync(cts.Token)).StatusCode);

            // 推一帧视频（SPS/PPS 已在 SDP 里）与一帧 AAC
            var sequence = 0;
            foreach (var nal in AnnexB.Split(TestData.AnnexBKeyFrame()))
            {
                await client.SendInterleavedAsync(
                    0,
                    BuildRtp(96, (ushort)++sequence, 90_000, 0x11223344, nal, marker: true),
                    cts.Token);
            }

            byte[] aacPayload = [0x00, 0x10, 0x00, 0x05, 0x11, 0x22, 0x33, 0x44, 0x55];
            await client.SendInterleavedAsync(
                2,
                BuildRtp(97, 1, 48_000, 0x11223344, aacPayload, marker: true),
                cts.Token);

            var key = StreamKey.Create("__defaultVhost__", "live", "test");
            Assert.True(
                await WaitAsync(
                    () => Task.FromResult(
                        registry.TryGet(key, out var source) &&
                        source is not null &&
                        source.Video.Sps is not null &&
                        source.Audio.AudioSpecificConfig is not null),
                    TimeSpan.FromSeconds(5),
                    cts.Token),
                "推流会话没有写入视频/音频轨");

            Assert.True(registry.TryGet(key, out var pushed));
            Assert.NotNull(pushed);
            Assert.Equal(CodecId.Aac, pushed!.Audio.CodecId);
            Assert.Equal(48_000, pushed.Audio.SampleRate);
            Assert.Equal(2, pushed.Audio.Channels);

            // RECORD 后按 RtcpInterval 周期发 RR：通道 1（视频）或 3（音频），PT=201
            var (channel, payload) = await client.ReadInterleavedAsync(cts.Token);
            Assert.Contains(channel, new[] { 1, 3 });
            Assert.Equal(201, payload[1]);
            Assert.Equal(32, payload.Length);
        }
        finally
        {
            await client.DisposeAsync();
            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }
        }
    }

    private static string BuildSdp(bool withAudio = true)
    {
        var sdp = SdpWithAudio
            .Replace("{SPS}", Convert.ToBase64String(TestData.Sps720), StringComparison.Ordinal)
            .Replace("{PPS}", Convert.ToBase64String(TestData.Pps), StringComparison.Ordinal);

        if (withAudio)
        {
            return sdp;
        }

        var index = sdp.IndexOf("\nm=audio", StringComparison.Ordinal);
        return index < 0 ? sdp : sdp[..index] + "\r\n";
    }

    private static byte[] BuildRtp(int payloadType, ushort sequence, uint timestamp, uint ssrc, byte[] payload, bool marker)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = (byte)((marker ? 0x80 : 0) | (payloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
        payload.CopyTo(packet, 12);
        return packet;
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

            await Task.Delay(50, cancellationToken);
        }

        return false;
    }
}
