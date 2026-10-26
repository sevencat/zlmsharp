using System.Buffers;
using System.Text;
using ZlmSharp.Codecs;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Flv;
using ZlmSharp.Protocols.Rtmp;
using ZlmSharp.Protocols.Rtsp;
using ZlmSharp.Recording;

namespace ZlmSharp.Tests;

/// <summary>H.265 全链路：RTP（RFC 7798）/ SDP / FLV / MP4 / TS / RTSP 推流播放。</summary>
public class H265PipelineTests
{
    private static H265Config VideoConfig => new()
    {
        Vps = TestData.H265Vps,
        Sps = TestData.H265Sps,
        Pps = TestData.H265Pps,
    };

    [Fact]
    public void Rtp_Hevc_RoundTripsSingleNalAndFragments()
    {
        var annexB = Concat(TestData.AnnexBH265Config(), TestData.AnnexBH265KeyFrame(), TestData.AnnexBH265InterFrame());
        var packetizer = new RtpPacketizer(0x1234, payloadType: 96, mtu: 200, sequenceNumber: 1, codec: CodecId.H265);
        var packets = packetizer.Packetize(annexB, timestamp: 9000);

        Assert.True(packets.Count > 4, "MTU 200 时大 NAL 应该被分片");

        var depacketizer = new H265RtpDepacketizer();
        var accessUnits = new List<(byte[] Data, bool Key)>();
        foreach (var packet in packets)
        {
            var header = new RtpPacketHeader();
            Assert.True(RtpPacketHeader.TryParse(packet, out header));
            var payload = packet.AsSpan(header.PayloadOffset, header.PayloadLength);

            if (depacketizer.Push(payload, header.Marker, out var accessUnit, out var keyFrame) && accessUnit is not null)
            {
                accessUnits.Add((accessUnit, keyFrame));
            }
        }

        Assert.NotEmpty(accessUnits);
        // Packetize 把整段 Annex-B 当作一个访问单元：VPS/SPS/PPS + IDR + 非关键帧
        var nals = AnnexB.Split(accessUnits[0].Data);
        Assert.Equal(5, nals.Count);
        Assert.Equal(H265NalUnitType.Vps, H265NalUnit.GetType(nals[0]));
        Assert.Equal(H265NalUnitType.Sps, H265NalUnit.GetType(nals[1]));
        Assert.Equal(H265NalUnitType.Pps, H265NalUnit.GetType(nals[2]));
        Assert.Equal(H265NalUnitType.IdrWRadl, H265NalUnit.GetType(nals[3]));
        Assert.True(accessUnits[0].Key, "关键帧标记丢失");
    }

    [Fact]
    public void Sdp_H265_BuildsAndParsesParameterSets()
    {
        var sdp = SdpBuilder.BuildH265(VideoConfig);

        Assert.Contains("H265/90000", sdp);
        Assert.Contains("sprop-vps=", sdp);
        Assert.Contains("sprop-sps=", sdp);

        Assert.True(SdpParser.TryParse(sdp, out var description, out var error), error);
        Assert.NotNull(description);
        Assert.True(description!.Video!.IsH265);
        Assert.Equal(CodecId.H265, description.Video.Codec);
        Assert.True(description.Video.HasParameterSets);
        Assert.Equal(TestData.H265Vps, description.Video.Vps);
        Assert.Equal(TestData.H265Sps, description.Video.Sps);
        Assert.Equal(TestData.H265Pps, description.Video.Pps);

        // 兼容接口 TryParseH264 仍然只认 H.264
        Assert.False(SdpParser.TryParseH264(sdp, out _, out _));
    }

    [Fact]
    public void FlvMuxer_Hevc_WritesHvcSequenceHeaderAndCodecId()
    {
        var muxer = new FlvMuxer(VideoCodecConfig.FromH265(VideoConfig), new FlvMetadata { Width = 320, Height = 240 });

        Assert.Equal(FlvConstants.VideoCodecHevc, muxer.VideoCodecId);

        var configBody = muxer.BuildAvcConfigBody();
        Assert.Equal(0x1C, configBody[0]); // keyframe + codec 12
        Assert.Equal(0, configBody[1]);    // sequence header
        Assert.True(H265Config.TryParseConfigRecord(configBody.AsSpan(5), out var parsed, out var error), error);
        Assert.Equal(TestData.H265Sps, parsed!.Sps);

        // 元数据里的 videocodecid 也应该是 12
        var metadata = Encoding.ASCII.GetString(muxer.BuildMetadataBody());
        Assert.Contains("videocodecid", metadata);

        var frame = MediaFrame.Create(TestData.AnnexBH265KeyFrame(), TrackType.Video, CodecId.H265, 0, 0, FrameFlags.Key);
        var buffer = new ArrayBufferWriter<byte>(1024);
        var written = muxer.WriteVideoBody(frame, buffer);
        Assert.True(written > 0);
        Assert.Equal((byte)((1 << 4) | FlvConstants.VideoCodecHevc), buffer.WrittenSpan[0]);
        Assert.Equal(1, buffer.WrittenSpan[1]);
    }

    [Fact]
    public void FlvIngest_HevcConfigThenSamplesUpdatesSource()
    {
        var source = new MediaSource(StreamKey.Create(null, "live", "hevc"), "rtsp");
        var ingest = new FlvFrameIngest(source);

        var muxer = new FlvMuxer(VideoCodecConfig.FromH265(VideoConfig), new FlvMetadata { Width = 320, Height = 240 });
        ingest.Video(muxer.BuildAvcConfigBody(), 0);

        Assert.Equal(CodecId.H265, source.Video.CodecId);
        Assert.NotNull(source.Video.Vps);
        Assert.Equal(320, source.Video.Width);
        Assert.Equal(240, source.Video.Height);
        Assert.NotNull(ingest.VideoConfig);

        // 构造 codecId=12 的视频负载：5 字节 FLV 头 + 长度前缀 NAL
        var keyNal = AnnexB.Split(TestData.AnnexBH265KeyFrame())[0];
        var payload = new byte[5 + 4 + keyNal.Length];
        payload[0] = (byte)((1 << 4) | FlvConstants.VideoCodecHevc);
        payload[1] = 1;
        payload[5] = (byte)(keyNal.Length >> 24);
        payload[6] = (byte)(keyNal.Length >> 16);
        payload[7] = (byte)(keyNal.Length >> 8);
        payload[8] = (byte)keyNal.Length;
        keyNal.CopyTo(payload, 9);

        ingest.Video(payload, 40);

        var gop = source.SnapshotGop();
        try
        {
            Assert.True(gop.Count >= 2);
            var last = gop[^1];
            Assert.Equal(CodecId.H265, last.CodecId);
            Assert.True(last.IsKeyFrame);
        }
        finally
        {
            foreach (var frame in gop)
            {
                frame.Dispose();
            }
        }

    }

    [Fact]
    public void Mp4Writer_Hevc_WritesHvc1AndHvcC()
    {
        using var stream = new MemoryStream();
        using (var writer = new Mp4Writer(stream, VideoCodecConfig.FromH265(VideoConfig), 320, 240, ownsStream: false))
        {
            writer.WriteVideo(TestData.AnnexBH265KeyFrame(), 0, 0, keyFrame: true);
            writer.WriteVideo(TestData.AnnexBH265InterFrame(), 40, 40, keyFrame: false);
            writer.Complete();

            Assert.Equal(2, writer.VideoSampleCount);
        }

        var bytes = stream.ToArray();
        Assert.Contains("hvc1", Encoding.ASCII.GetString(bytes));
        Assert.Contains("hvcC", Encoding.ASCII.GetString(bytes));
        Assert.DoesNotContain("avcC", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void TsMuxer_Hevc_UsesStreamType0x24()
    {
        using var stream = new MemoryStream();
        using (var muxer = new TsMuxer(stream, null, 0, 0, CodecId.H265))
        {
            muxer.WriteVideo(TestData.AnnexBH265KeyFrame(), 0, 0, keyFrame: true);
        }

        var bytes = stream.ToArray();
        Assert.Equal(0, bytes.Length % 188);

        var pmtPayload = -1;
        for (var offset = 0; offset + 188 <= bytes.Length; offset += 188)
        {
            if (bytes[offset] != 0x47)
            {
                continue;
            }

            var pid = ((bytes[offset + 1] & 0x1F) << 8) | bytes[offset + 2];
            if (pid != 0x1000)
            {
                continue;
            }

            var adaptation = (bytes[offset + 3] >> 4) & 0x03;
            var position = offset + 4;
            if ((adaptation & 0x02) != 0)
            {
                position += 1 + bytes[position];
            }

            pmtPayload = position;
            break;
        }

        Assert.True(pmtPayload >= 0, "没有找到 PMT 包");
        Assert.Equal(0x00, bytes[pmtPayload]);          // pointer_field
        Assert.Equal(0x02, bytes[pmtPayload + 1]);      // table_id
        Assert.Equal(0x24, bytes[pmtPayload + 13]);     // stream_type = HEVC
    }

    [Fact]
    public async Task RtspPushThenPlay_Hevc_EndToEnd()
    {
        var rtmpPort = TestHost.GetFreePort();
        var rtspPort = TestHost.GetFreePort();
        var origin = $"rtsp://127.0.0.1:{rtspPort}/live/hevc";

        await using var host = await TestHost.StartAsync(rtmpPort, options => options.Rtsp.Port = rtspPort);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var push = await RtspTestClient.ConnectAsync("127.0.0.1", rtspPort, cts.Token);
        try
        {
            await push.SendAsync(
                "ANNOUNCE",
                origin,
                SdpBuilder.BuildH265(VideoConfig),
                cts.Token,
                ("Content-Type", "application/sdp"));
            Assert.Equal(200, (await push.ReadResponseAsync(cts.Token)).StatusCode);

            await push.SendAsync(
                "SETUP",
                origin + "/trackID=0",
                cts.Token,
                ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
            Assert.Equal(200, (await push.ReadResponseAsync(cts.Token)).StatusCode);

            await push.SendAsync("RECORD", origin, cts.Token, ("Session", "hevc-test"));
            Assert.Equal(200, (await push.ReadResponseAsync(cts.Token)).StatusCode);

            var packetizer = new RtpPacketizer(0x2233, payloadType: 96, codec: CodecId.H265);
            foreach (var packet in packetizer.Packetize(TestData.AnnexBH265Config(), 0))
            {
                await push.SendInterleavedAsync(0, packet, cts.Token);
            }

            for (var index = 0; index < 5; index++)
            {
                var frame = index == 0 ? TestData.AnnexBH265KeyFrame() : TestData.AnnexBH265InterFrame();
                foreach (var packet in packetizer.Packetize(frame, (uint)(index * 3600)))
                {
                    await push.SendInterleavedAsync(0, packet, cts.Token);
                }
            }

            var play = await RtspTestClient.ConnectAsync("127.0.0.1", rtspPort, cts.Token);
            try
            {
                await play.SendAsync("DESCRIBE", origin, cts.Token, ("Accept", "application/sdp"));
                var describe = await play.ReadResponseAsync(cts.Token);
                Assert.Equal(200, describe.StatusCode);
                Assert.Contains("H265/90000", describe.Body);
                Assert.Contains("sprop-vps=", describe.Body);

                await play.SendAsync(
                    "SETUP",
                    origin + "/trackID=0",
                    cts.Token,
                    ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"));
                Assert.Equal(200, (await play.ReadResponseAsync(cts.Token)).StatusCode);

                await play.SendAsync("PLAY", origin, cts.Token, ("Session", "hevc-play"));
                Assert.Equal(200, (await play.ReadResponseAsync(cts.Token)).StatusCode);

                var depacketizer = new H265RtpDepacketizer();
                var gotKeyFrame = false;
                for (var index = 0; index < 64 && !gotKeyFrame; index++)
                {
                    var rtp = await play.ReadInterleavedPayloadAsync(0, cts.Token);
                    Assert.True(RtpPacketHeader.TryParse(rtp, out var header));
                    if (depacketizer.Push(
                            rtp.AsSpan(header.PayloadOffset, header.PayloadLength),
                            header.Marker,
                            out var accessUnit,
                            out var keyFrame) &&
                        accessUnit is not null)
                    {
                        gotKeyFrame = keyFrame;
                    }
                }

                Assert.True(gotKeyFrame, "RTSP 播放没有收到 HEVC 关键帧");
            }
            finally
            {
                await play.DisposeAsync();
            }
        }
        finally
        {
            await push.DisposeAsync();
        }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = parts.Sum(part => part.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
