using System.Buffers.Binary;
using ZlmSharp.Configuration;
using ZlmSharp.Recording;

namespace ZlmSharp.Tests;

/// <summary>录制二期：TS 封装、HLS 切片/m3u8、录像文件浏览与删除。</summary>
public class HlsRecorderTests
{
    [Fact]
    public void TsMuxer_WritesAlignedPacketsWithPsiAndPes()
    {
        using var buffer = new MemoryStream();
        var muxer = new TsMuxer(buffer, null, 0, 0);

        muxer.WriteVideo(TestData.AnnexBKeyFrame(), 0, 0, keyFrame: true);
        muxer.WriteVideo(TestData.AnnexBInterFrame(), 40, 40, keyFrame: false);
        muxer.Flush();

        var bytes = buffer.ToArray();
        Assert.True(bytes.Length > 0);
        Assert.Equal(0, bytes.Length % 188);
        Assert.Equal(0x47, bytes[0]);

        // 第一个包是 PAT（PID 0），pointer_field 后 table_id=0
        Assert.Equal(0x00, bytes[1] & 0x1F);
        Assert.Equal(0x40, bytes[1] & 0x40);
        var patPayload = PayloadOffset(bytes, 0);
        Assert.Equal(0x00, bytes[patPayload]); // pointer_field
        Assert.Equal(0x00, bytes[patPayload + 1]); // table_id PAT

        // PMT 在 PID 0x1000，声明 H.264（0x1B）与 PCR_PID
        var pmtOffset = FindPacketOffset(bytes, 0x1000);
        Assert.True(pmtOffset >= 0, "没有找到 PMT 包");
        var pmtPayload = PayloadOffset(bytes, pmtOffset);
        Assert.Equal(0x02, bytes[pmtPayload + 1]);
        // pointer_field(1) + table_id..program_info_length(12) → 第一个 stream_type
        Assert.Equal(0x1B, bytes[pmtPayload + 13]);

        // 视频 PES：00 00 01 E0，PTS/DTS 都在
        var videoOffset = FindPacketOffset(bytes, 0x100);
        Assert.True(videoOffset >= 0, "没有找到视频包");
        var pesStart = PayloadOffset(bytes, videoOffset);

        Assert.Equal(0x00, bytes[pesStart]);
        Assert.Equal(0x00, bytes[pesStart + 1]);
        Assert.Equal(0x01, bytes[pesStart + 2]);
        Assert.Equal(0xE0, bytes[pesStart + 3]);
        Assert.Equal(0xC0, bytes[pesStart + 7]);
    }

    [Fact]
    public void TsMuxer_WritesAdtsForAudio()
    {
        using var buffer = new MemoryStream();
        // AAC LC 48kHz 立体声（ASC 0x11 0x90）
        var muxer = new TsMuxer(buffer, [0x11, 0x90], 48_000, 2);

        muxer.WriteVideo(TestData.AnnexBKeyFrame(), 0, 0, keyFrame: true);
        muxer.WriteAudio([0xAA, 0xBB, 0xCC, 0xDD], 0);
        muxer.Flush();

        var bytes = buffer.ToArray();
        var pmtOffset = FindPacketOffset(bytes, 0x1000);
        Assert.True(pmtOffset >= 0);
        var pmtPayload = PayloadOffset(bytes, pmtOffset);
        Assert.Equal(0x1B, bytes[pmtPayload + 13]); // 视频
        Assert.Equal(0x0F, bytes[pmtPayload + 18]); // 音频（AAC/ADTS）

        var audioOffset = FindPacketOffset(bytes, 0x101);
        Assert.True(audioOffset >= 0, "没有找到音频包");

        var pesStart = PayloadOffset(bytes, audioOffset);
        Assert.Equal(0xC0, bytes[pesStart + 3]);

        // PES 头 14 字节后是 ADTS：FF F1 ...
        var adts = pesStart + 14;
        Assert.Equal(0xFF, bytes[adts]);
        Assert.Equal(0xF1, bytes[adts + 1]);
        Assert.Equal(3, (bytes[adts + 2] >> 2) & 0x0F); // 采样率索引 3 = 48kHz
    }

    [Fact]
    public void HlsPackage_RollsSegmentsAndWritesPlaylist()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"zlmsharp-hls-{Guid.NewGuid():N}");

        try
        {
            var playlist = Path.Combine(directory, "hls.m3u8");
            var frame = TestData.AnnexBKeyFrame();

            using (var hls = new HlsPackage(directory, "hls", segmentSeconds: 1, segmentCount: 2, null, 0, 0))
            {
                // 每 500ms 一个关键帧：0、500 在首片，1000/1500 切到第二片，2000/2500 切到第三片
                for (var index = 0; index < 6; index++)
                {
                    var dts = index * 500L;
                    hls.WriteVideo(frame, dts, dts, keyFrame: true);
                }
            }

            Assert.True(File.Exists(playlist));

            var content = File.ReadAllText(playlist);
            Assert.Contains("#EXTM3U", content);
            Assert.Contains("#EXT-X-TARGETDURATION:1", content);
            Assert.Contains("#EXTINF:", content);
            Assert.Contains("#EXT-X-ENDLIST", content);

            var segments = Directory.GetFiles(directory, "*.ts");
            Assert.Equal(2, segments.Length); // seg_num=2，第三片时最旧的被删

            // 保留的是最后两个切片
            Assert.Contains("hls-000002.ts", content);
            Assert.Contains("hls-000003.ts", content);
            Assert.DoesNotContain("hls-000001.ts", content);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void HlsPackage_WaitsForKeyFrameBeforeStarting()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"zlmsharp-hls-{Guid.NewGuid():N}");

        try
        {
            using (var hls = new HlsPackage(directory, "hls", 1, 3, null, 0, 0))
            {
                hls.WriteVideo(TestData.AnnexBInterFrame(), 0, 0, keyFrame: false);
                Assert.Null(hls.CurrentSegmentPath);

                hls.WriteVideo(TestData.AnnexBKeyFrame(), 40, 40, keyFrame: true);
                Assert.NotNull(hls.CurrentSegmentPath);
            }

            Assert.Single(Directory.GetFiles(directory, "*.ts"));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void RecordFileService_ListsAndDeletesInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"zlmsharp-record-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "live", "test", "2026-10-27"));

        var file = Path.Combine(root, "live", "test", "2026-10-27", "00-00-00-001.mp4");
        File.WriteAllBytes(file, [1, 2, 3]);

        try
        {
            var options = new AppOptions();
            options.Protocol.Mp4SavePath = root;
            var service = new RecordFileService(options);

            var dates = service.List("live", "test", period: string.Empty);
            Assert.Equal(new[] { "2026-10-27" }, dates);

            var files = service.List("live", "test", period: "2026-10-27");
            Assert.Equal(new[] { "00-00-00-001.mp4" }, files);

            Assert.True(service.Delete("live/test/2026-10-27/00-00-00-001.mp4"));
            Assert.False(File.Exists(file));

            // 目录穿越必须被拒绝
            Assert.False(service.Delete("../outside.txt"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>跳过 TS 头与 adaptation field，返回 payload 起始下标。</summary>
    private static int PayloadOffset(byte[] bytes, int packetOffset)
    {
        var position = 4;
        if ((bytes[packetOffset + 3] & 0x20) != 0)
        {
            position += 1 + bytes[packetOffset + 4];
        }

        return packetOffset + position;
    }

    private static int FindPacketOffset(byte[] bytes, int pid)
    {
        for (var offset = 0; offset + 188 <= bytes.Length; offset += 188)
        {
            if (bytes[offset] != 0x47)
            {
                continue;
            }

            var packetPid = ((bytes[offset + 1] & 0x1F) << 8) | bytes[offset + 2];
            if (packetPid == pid)
            {
                return offset;
            }
        }

        return -1;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
