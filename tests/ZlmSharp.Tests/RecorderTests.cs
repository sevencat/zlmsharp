using System.Buffers.Binary;
using System.Text;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Recording;

namespace ZlmSharp.Tests;

/// <summary>录像：MP4/FLV 同时落盘、带 AAC、按 max_second 切片。</summary>
public class RecorderTests
{
    private static readonly byte[] AudioConfig = [0x12, 0x10]; // AAC-LC / 44.1kHz / stereo
    private static readonly byte[] AudioFrame = [0x21, 0x10, 0x04, 0x60];

    [Fact]
    public void Start_UnknownStream_IsRejected()
    {
        var options = new AppOptions();
        using var manager = new RecorderManager(new MediaSourceRegistry(), options);

        var exception = Assert.Throws<InvalidOperationException>(
            () => manager.Start(StreamKey.Create(string.Empty, "live", "cam"), RecordFormat.Mp4));
        Assert.Contains("不在线", exception.Message);
    }

    [Fact]
    public void Start_WithoutKeyFrame_IsRejected()
    {
        var registry = new MediaSourceRegistry();
        var key = StreamKey.Create(string.Empty, "live", "cam");
        registry.GetOrCreate(key, "rtmp");
        using var manager = new RecorderManager(registry, new AppOptions());

        var exception = Assert.Throws<InvalidOperationException>(() => manager.Start(key, RecordFormat.Mp4));
        Assert.Contains("参数集", exception.Message);
    }

    [Fact]
    public void RecordsMp4AndFlv_WithAudio()
    {
        var root = CreateTempRoot();

        try
        {
            var registry = new MediaSourceRegistry();
            var key = StreamKey.Create(string.Empty, "live", "cam");
            var source = FillSource(registry, key);

            var options = new AppOptions();
            options.Env.RecordRoot = root;
            using var manager = new RecorderManager(registry, options);

            var started = manager.Start(key, RecordFormat.All, maxSeconds: 3600);
            Assert.True(started.Recording);
            Assert.NotNull(started.Mp4Path);
            Assert.NotNull(started.FlvPath);

            var current = WaitForFrames(manager, key, 4);

            Assert.True(manager.Stop(key.Vhost, key.App, key.Stream));
            source.Close();

            var mp4 = new FileInfo(current.Mp4Path!);
            var flv = new FileInfo(current.FlvPath!);
            Assert.True(mp4.Exists && mp4.Length > 200, $"mp4 size={mp4.Length}");
            Assert.True(flv.Exists && flv.Length > 100, $"flv size={flv.Length}");

            var mp4Bytes = File.ReadAllBytes(mp4.FullName);
            Assert.Equal("ftyp", Ascii(mp4Bytes, 4, 4));
            Assert.Contains("moov", Encoding.ASCII.GetString(mp4Bytes));
            Assert.Contains("avcC", Encoding.ASCII.GetString(mp4Bytes));
            Assert.Contains("esds", Encoding.ASCII.GetString(mp4Bytes));

            // stsz 的 sample_count：视频 3 帧、音频 1 帧
            var counts = FindSampleCounts(mp4Bytes).Order().ToArray();
            Assert.Equal(new[] { 1, 3 }, counts);

            var flvBytes = File.ReadAllBytes(flv.FullName);
            Assert.Equal("FLV", Ascii(flvBytes, 0, 3));
            Assert.Equal(0x05, flvBytes[4]); // 视频 + 音频

            Assert.False(manager.Get(key)?.Recording == true);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MaxSecond_RotatesSlices()
    {
        var root = CreateTempRoot();

        try
        {
            var registry = new MediaSourceRegistry();
            var key = StreamKey.Create(string.Empty, "live", "cam");
            var source = FillSource(registry, key);

            var options = new AppOptions();
            options.Env.RecordRoot = root;
            using var manager = new RecorderManager(registry, options);

            manager.Start(key, RecordFormat.All, maxSeconds: 10);
            WaitForFrames(manager, key, 4);

            // 10 秒后触发新切片
            source.AddFrame(MediaFrame.Create(TestData.AnnexBKeyFrame(), TrackType.Video, CodecId.H264, 12_000, 12_000, FrameFlags.Key));
            source.AddFrame(MediaFrame.Create(TestData.AnnexBInterFrame(), TrackType.Video, CodecId.H264, 12_040, 12_040));
            WaitForFrames(manager, key, 6);

            Assert.True(manager.Stop(key.Vhost, key.App, key.Stream));
            source.Close();

            var directory = Path.GetDirectoryName(Directory.GetFiles(root, "*.mp4", SearchOption.AllDirectories)[0])!;
            Assert.Equal(2, Directory.GetFiles(directory, "*.mp4").Length);
            Assert.Equal(2, Directory.GetFiles(directory, "*.flv").Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Stop_WithoutSession_ReturnsFalse()
    {
        using var manager = new RecorderManager(new MediaSourceRegistry(), new AppOptions());
        Assert.False(manager.Stop(string.Empty, "live", "cam"));
        Assert.Empty(manager.List());
        Assert.Null(manager.Get(string.Empty, "live", "cam"));
    }

    private static MediaSource FillSource(MediaSourceRegistry registry, StreamKey key)
    {
        var source = registry.GetOrCreate(key, "rtmp");
        source.Video.SetVideoSize(1280, 720);
        source.Video.SetH264Config(TestData.Sps720, TestData.Pps);
        source.Audio.SetAudioInfo(44100, 2);
        source.Audio.SetAudioConfig(AudioConfig);

        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBConfig(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Config | FrameFlags.Key));
        source.AddFrame(MediaFrame.Create(
            AudioConfig, TrackType.Audio, CodecId.Aac, 0, 0, FrameFlags.Config));
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBKeyFrame(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Key));
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBInterFrame(), TrackType.Video, CodecId.H264, 40, 40));
        source.AddFrame(MediaFrame.Create(
            AudioFrame, TrackType.Audio, CodecId.Aac, 0, 0));
        source.AddFrame(MediaFrame.Create(
            TestData.AnnexBInterFrame(), TrackType.Video, CodecId.H264, 80, 80));

        return source;
    }

    private static RecordingStatus WaitForFrames(RecorderManager manager, StreamKey key, long frames)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        RecordingStatus? status;
        do
        {
            status = manager.Get(key);
            if (status is not null && status.Frames >= frames)
            {
                return status;
            }

            Thread.Sleep(20);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail($"等待录像帧数超时：frames={status?.Frames ?? 0}, error={status?.Error}");
        throw new InvalidOperationException();
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "zlmsharp-record-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Ascii(byte[] data, int offset, int length) =>
        Encoding.ASCII.GetString(data, offset, length);

    private static IEnumerable<int> FindSampleCounts(byte[] data)
    {
        for (var index = 4; index + 16 <= data.Length; index++)
        {
            if (data[index] == (byte)'s' && data[index + 1] == (byte)'t' &&
                data[index + 2] == (byte)'s' && data[index + 3] == (byte)'z')
            {
                yield return (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index + 12));
            }
        }
    }
}
