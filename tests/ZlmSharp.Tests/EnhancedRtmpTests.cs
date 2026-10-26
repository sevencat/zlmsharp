using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Codecs;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Flv;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Tests;

/// <summary>
/// Enhanced RTMP（E-RTMP）：HEVC 用 [0x80|FrameType|PacketType]['hvc1'][CTS:3?] 头，
/// 覆盖封装、解封装、RTMP 播放与 HTTP-FLV 输出。
/// </summary>
public class EnhancedRtmpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static H265Config HevcConfig => new()
    {
        Vps = TestData.H265Vps,
        Sps = TestData.H265Sps,
        Pps = TestData.H265Pps,
    };

    private static FlvMuxer HevcMuxer(bool enhanced) => new(
        VideoCodecConfig.FromH265(HevcConfig),
        new FlvMetadata { Width = 320, Height = 240, FrameRate = 25 },
        enhancedRtmp: enhanced);

    private static MediaFrame HevcFrame(byte[] annexB, long dts, long pts, bool key) =>
        MediaFrame.Create(
            annexB,
            TrackType.Video,
            CodecId.H265,
            dts,
            pts,
            key ? FrameFlags.Key : FrameFlags.None);

    [Fact]
    public void EnhancedSequenceHeader_UsesExHeaderFlagAndHvc1FourCc()
    {
        var body = HevcMuxer(enhanced: true).BuildAvcConfigBody();

        Assert.Equal(0x90, body[0]); // 0x80 | key(0x10) | SequenceStart(0)
        Assert.Equal("hvc1", Encoding.ASCII.GetString(body, 1, 4));
        Assert.True(H265Config.TryParseConfigRecord(body.AsSpan(5), out var parsed, out var error), error);
        Assert.Equal(TestData.H265Sps, parsed!.Sps);

        Assert.True(FlvVideoTagHeader.TryParse(body, out var header));
        Assert.True(header.IsEnhanced);
        Assert.True(header.IsHevc);
        Assert.True(header.IsSequenceHeader);
        Assert.Equal(5, header.PayloadOffset);
        Assert.Equal(0, header.CompositionTime);
    }

    [Fact]
    public void EnhancedCodedFrames_OmitCompositionTimeWhenZero()
    {
        var muxer = HevcMuxer(enhanced: true);
        var buffer = new ArrayBufferWriter<byte>(4096);

        using (var frame = HevcFrame(TestData.AnnexBH265KeyFrame(), 0, 0, key: true))
        {
            Assert.True(muxer.WriteVideoBody(frame, buffer) > 0);
        }

        var span = buffer.WrittenSpan;
        Assert.Equal(0x93, span[0]); // 0x80 | key(0x10) | CodedFramesX(3)
        Assert.Equal("hvc1", Encoding.ASCII.GetString(span.Slice(1, 4).ToArray()));

        Assert.True(FlvVideoTagHeader.TryParse(span, out var header));
        Assert.True(header.IsCodedFrames);
        Assert.Equal(0, header.CompositionTime);
        Assert.Equal(5, header.PayloadOffset);

        // 负载与 legacy 模式（5 字节头之后）完全一致
        var legacyBuffer = new ArrayBufferWriter<byte>(4096);
        using (var frame = HevcFrame(TestData.AnnexBH265KeyFrame(), 0, 0, key: true))
        {
            Assert.True(HevcMuxer(enhanced: false).WriteVideoBody(frame, legacyBuffer) > 0);
        }

        Assert.Equal(legacyBuffer.WrittenSpan[5..].ToArray(), span[5..].ToArray());
    }

    [Fact]
    public void EnhancedCodedFrames_WriteCompositionTimeWhenPtsDiffersFromDts()
    {
        var muxer = HevcMuxer(enhanced: true);
        var buffer = new ArrayBufferWriter<byte>(4096);

        using (var frame = HevcFrame(TestData.AnnexBH265InterFrame(), 1000, 1040, key: false))
        {
            Assert.True(muxer.WriteVideoBody(frame, buffer) > 0);
        }

        var span = buffer.WrittenSpan;
        Assert.Equal(0xA1, span[0]); // 0x80 | inter(0x20) | CodedFrames(1)

        Assert.True(FlvVideoTagHeader.TryParse(span, out var header));
        Assert.Equal(40, header.CompositionTime);
        Assert.Equal(8, header.PayloadOffset);
        Assert.True(header.IsHevc);
    }

    [Fact]
    public void EnhancedOff_KeepsLegacyHevcHeaderAndH264IsAlwaysLegacy()
    {
        var legacy = HevcMuxer(enhanced: false);
        Assert.False(legacy.EnhancedRtmp);

        var config = legacy.BuildAvcConfigBody();
        Assert.Equal(0x1C, config[0]); // keyframe + codecId 12
        Assert.Equal(0, config[1]);    // sequence header
        Assert.Equal(0, config[2]);    // composition time

        // 打开增强开关也不影响 H.264（兼容性最好，规范里 avc1 只用于多轨）
        var h264 = new FlvMuxer(
            new H264Config { Sps = TestData.Sps720, Pps = TestData.Pps },
            new FlvMetadata { Width = 1280, Height = 720 },
            enhancedRtmp: true);

        Assert.False(h264.EnhancedRtmp);
        Assert.Equal(0x17, h264.BuildAvcConfigBody()[0]);
    }

    [Fact]
    public void Metadata_UsesFourCcNumberInEnhancedModeAndLegacyIdOtherwise()
    {
        // 与 FFmpeg/OBS 对齐：增强模式 videocodecid = 'hvc1' 的整数值
        Assert.Equal(1752589105d, ReadVideoCodecId(HevcMuxer(enhanced: true)));
        Assert.Equal(12d, ReadVideoCodecId(HevcMuxer(enhanced: false)));
    }

    [Fact]
    public void Ingest_EnhancedHevc_UpdatesSourceAndCarriesCompositionTime()
    {
        var source = new MediaSource(StreamKey.Create(null, "live", "hevc-enhanced"), "rtmp");
        var ingest = new FlvFrameIngest(source);
        var muxer = HevcMuxer(enhanced: true);

        ingest.Video(muxer.BuildAvcConfigBody(), 0);

        Assert.Equal(CodecId.H265, source.Video.CodecId);
        Assert.Equal(TestData.H265Vps, source.Video.Vps);
        Assert.Equal(320, source.Video.Width);
        Assert.Equal(240, source.Video.Height);

        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var frame = HevcFrame(TestData.AnnexBH265KeyFrame(), 0, 40, key: true))
        {
            Assert.True(muxer.WriteVideoBody(frame, buffer) > 0);
        }

        ingest.Video(buffer.WrittenSpan, 0);

        var gop = source.SnapshotGop();
        try
        {
            var last = gop[^1];
            Assert.Equal(CodecId.H265, last.CodecId);
            Assert.True(last.IsKeyFrame);
            Assert.Equal(0, last.Dts);
            Assert.Equal(40, last.Pts);
            Assert.Equal(TestData.AnnexBH265KeyFrame(), last.Data.ToArray());
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
    public void Ingest_IgnoresEnhancedMetadataAndSequenceEnd()
    {
        var source = new MediaSource(StreamKey.Create(null, "live", "hevc-meta"), "rtmp");
        var ingest = new FlvFrameIngest(source);

        var metadata = BuildEnhancedPacket(FlvVideoPacketType.Metadata, frameType: 5);
        var sequenceEnd = BuildEnhancedPacket(FlvVideoPacketType.SequenceEnd, frameType: 1);

        ingest.Video(metadata, 0);
        ingest.Video(sequenceEnd, 0);

        Assert.Empty(source.SnapshotGop());
    }

    [Fact]
    public async Task RtmpPlay_Hevc_SendsEnhancedHeader()
    {
        var registry = new MediaSourceRegistry();
        var key = StreamKey.Create(null, "live", "hevc");
        var source = registry.GetOrCreate(key, "rtsp");
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBH265Config(), TrackType.Video, CodecId.H265, 0, 0, FrameFlags.Config | FrameFlags.Key));
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBH265KeyFrame(), TrackType.Video, CodecId.H265, 0, 0, FrameFlags.Key));

        var (listener, serverTask, port) = StartSessionServer(registry, enhanced: true);

        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var client = await RtmpPlayClient.ConnectAsync("127.0.0.1", port, "live", cts.Token);

            try
            {
                await client.PlayAsync("hevc", cts.Token);

                while (true)
                {
                    var (code, _) = await client.ReadNextStatusAsync(cts.Token);
                    if (code == "NetStream.Play.Start")
                    {
                        break;
                    }
                }

                var metadata = await client.ReadMessageAsync(cts.Token);
                Assert.Equal(RtmpMessageType.DataAmf0, metadata!.Type);

                var configMessage = await client.ReadMessageAsync(cts.Token);
                Assert.Equal(RtmpMessageType.Video, configMessage!.Type);
                Assert.Equal(0x90, configMessage.Payload[0]);
                Assert.Equal("hvc1", Encoding.ASCII.GetString(configMessage.Payload, 1, 4));
                Assert.True(
                    H265Config.TryParseConfigRecord(configMessage.Payload.AsSpan(5), out var config, out var error),
                    error);
                Assert.Equal(TestData.H265Sps, config!.Sps);

                var keyMessage = await client.ReadMessageAsync(cts.Token);
                Assert.Equal(RtmpMessageType.Video, keyMessage!.Type);
                Assert.Equal(0x93, keyMessage.Payload[0]); // key + CodedFramesX（pts == dts）

                source.AddFrame(MediaFrame.Create(
                    TestData.AnnexBH265InterFrame(), TrackType.Video, CodecId.H265, 40, 80));

                var interMessage = await client.ReadMessageAsync(cts.Token);
                Assert.Equal(RtmpMessageType.Video, interMessage!.Type);
                Assert.Equal(0xA1, interMessage.Payload[0]); // inter + CodedFrames
                Assert.Equal(40, (interMessage.Payload[5] << 16) | (interMessage.Payload[6] << 8) | interMessage.Payload[7]);
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
            source.Close();
        }
    }

    [Fact]
    public async Task HttpFlv_Hevc_WritesEnhancedTags()
    {
        var rtmpPort = TestHost.GetFreePort();
        await using var host = await TestHost.StartAsync(rtmpPort, options =>
        {
            options.Rtmp.Enhanced = true;
            options.Rtsp.Port = 0;
        });

        var registry = host.Services.GetRequiredService<MediaSourceRegistry>();
        var source = registry.GetOrCreate(StreamKey.Create(null, "live", "hevc-flv"), "rtsp");
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBH265Config(), TrackType.Video, CodecId.H265, 0, 0, FrameFlags.Config | FrameFlags.Key));
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBH265KeyFrame(), TrackType.Video, CodecId.H265, 0, 0, FrameFlags.Key));

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync(
            $"{host.Address}/live/hevc-flv.live.flv",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        var tags = await ReadTagsAsync(stream, minimumTags: 3);

        Assert.True(tags.Count >= 3);
        Assert.Equal(FlvTagType.Video, tags[1].Type);
        Assert.Equal(0x90, tags[1].Body[0]);
        Assert.Equal("hvc1", Encoding.ASCII.GetString(tags[1].Body, 1, 4));

        Assert.Equal(FlvTagType.Video, tags[2].Type);
        Assert.Equal(0x93, tags[2].Body[0]);
        Assert.Equal(0u, tags[2].Timestamp);
    }

    private static double ReadVideoCodecId(FlvMuxer muxer)
    {
        var tag = Assert.Single(FlvTagParser.ParseAll(muxer.BuildMetadataTag()));
        Assert.True(Amf0.TryDecode(tag.Body, out var values, out var error), error);
        var metadata = Assert.IsType<Amf0EcmaArray>(values[1]);
        return metadata.GetNumberProperty("videocodecid");
    }

    private static byte[] BuildEnhancedPacket(FlvVideoPacketType packetType, int frameType)
    {
        var payload = new byte[8];
        payload[0] = (byte)(FlvConstants.VideoExHeaderFlag | (frameType << 4) | (byte)packetType);
        Encoding.ASCII.GetBytes("hvc1").CopyTo(payload, 1);
        return payload;
    }

    private static (TcpListener Listener, Task ServerTask, int Port) StartSessionServer(
        MediaSourceRegistry registry,
        bool enhanced)
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
                    MaxStreamWaitMS = 2000,
                    WaitTrackReadyMS = 2000,
                    Enhanced = enhanced,
                });
            await session.RunAsync(CancellationToken.None);
        });

        return (listener, serverTask, port);
    }

    private static async Task<List<FlvTag>> ReadTagsAsync(Stream stream, int minimumTags)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var collected = new List<byte>();
        var buffer = new byte[8192];

        while (DateTime.UtcNow < deadline)
        {
            var tags = FlvTagParser.ParseAll(collected.ToArray());
            if (tags.Count >= minimumTags)
            {
                return tags;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var read = await stream.ReadAsync(buffer, cts.Token);
            if (read == 0)
            {
                break;
            }

            collected.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return FlvTagParser.ParseAll(collected.ToArray());
    }
}
