using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Core;
using ZlmSharp.Recording;

namespace ZlmSharp.Tests;

/// <summary>录制二期端到端：HLS 产物、断流续录、录像文件浏览/删除 API。</summary>
public class RecordingPipelineTests
{
    private static readonly StreamKey Key = StreamKey.Create("__defaultVhost__", "live", "test");

    [Fact]
    public async Task StartRecord_Hls_ProducesPlaylistAndSegment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"zlmsharp-rec-{Guid.NewGuid():N}");
        var rtmpPort = TestHost.GetFreePort();

        try
        {
            await using var host = await TestHost.StartAsync(rtmpPort, options =>
            {
                options.Env.RecordRoot = root;
                options.Hls.SegDuration = 1;
                options.Hls.SegNum = 3;
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var publisher = await host.ConnectPublisherAsync(rtmpPort, cts.Token);
            await publisher.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
            await publisher.SendVideoAsync(TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var start = await client.GetStringAsync(
                $"{host.Address}/index/api/startRecord?vhost=__defaultVhost__&app=live&stream=test&type=4",
                cts.Token);
            Assert.Contains("\"code\":0", start);

            // 推满一个切片时长（seg_duration=1s），第一片收尾后才会写出 m3u8
            for (var index = 1; index <= 30; index++)
            {
                await publisher.SendVideoAsync(
                    TestData.AnnexBKeyFrame(),
                    keyFrame: true,
                    timestamp: (uint)(index * 40),
                    cancellationToken: cts.Token);
            }

            var playlist = Path.Combine(root, "live", "test", "hls", "hls.m3u8");
            Assert.True(
                await WaitAsync(() => Task.FromResult(File.Exists(playlist)), TimeSpan.FromSeconds(10), cts.Token),
                $"没有生成 HLS 播放列表：{playlist}");

            Assert.True(
                await WaitAsync(
                    () => Task.FromResult(Directory.Exists(Path.Combine(root, "live", "test", "hls")) &&
                                           Directory.GetFiles(Path.Combine(root, "live", "test", "hls"), "*.ts").Length > 0),
                    TimeSpan.FromSeconds(10),
                    cts.Token),
                "没有生成 TS 切片");

            var content = await File.ReadAllTextAsync(playlist, cts.Token);
            Assert.Contains("#EXTM3U", content);
            Assert.Contains("#EXTINF:", content);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task StartRecord_ResumesAfterStreamReconnects()
    {
        var root = Path.Combine(Path.GetTempPath(), $"zlmsharp-rec-{Guid.NewGuid():N}");
        var rtmpPort = TestHost.GetFreePort();

        try
        {
            await using var host = await TestHost.StartAsync(rtmpPort, options => options.Env.RecordRoot = root);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var manager = host.Services.GetRequiredService<RecorderManager>();

            var first = await host.ConnectPublisherAsync(rtmpPort, cts.Token);
            await first.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
            await first.SendVideoAsync(TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var start = await client.GetStringAsync(
                $"{host.Address}/index/api/startRecord?vhost=__defaultVhost__&app=live&stream=test&type=1",
                cts.Token);
            Assert.Contains("\"code\":0", start);
            Assert.True(manager.Get(Key)?.Recording == true, "录像没有启动");

            // 断流
            await first.DisposeAsync();
            Assert.True(
                await WaitAsync(
                    () => Task.FromResult(manager.Get(Key)?.Recording != true),
                    TimeSpan.FromSeconds(10),
                    cts.Token),
                "断流后录像会话没有结束");

            // 重新推流 → 应自动续录
            await using var second = await host.ConnectPublisherAsync(rtmpPort, cts.Token);
            await second.SendAvcConfigAsync(TestData.Sps720, TestData.Pps, cancellationToken: cts.Token);
            await second.SendVideoAsync(TestData.AnnexBKeyFrame(), keyFrame: true, timestamp: 0, cancellationToken: cts.Token);

            Assert.True(
                await WaitAsync(
                    () => Task.FromResult(manager.Get(Key)?.Recording == true),
                    TimeSpan.FromSeconds(15),
                    cts.Token),
                "流重新上线后没有续录");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RecordFileApi_ListsAndDeletes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"zlmsharp-rec-{Guid.NewGuid():N}");
        var rtmpPort = TestHost.GetFreePort();

        try
        {
            await using var host = await TestHost.StartAsync(rtmpPort, options => options.Env.RecordRoot = root);

            var directory = Path.Combine(root, "live", "test", "2026-10-27");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "00-00-00-001.mp4");
            await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var dates = await client.GetStringAsync(
                $"{host.Address}/index/api/getMp4RecordFile?app=live&stream=test&period=");
            Assert.Contains("2026-10-27", dates);

            var files = await client.GetStringAsync(
                $"{host.Address}/index/api/getMp4RecordFile?app=live&stream=test&period=2026-10-27");
            Assert.Contains("00-00-00-001.mp4", files);

            var deleted = await client.GetStringAsync(
                $"{host.Address}/index/api/deleteRecordFile?path=live/test/2026-10-27/00-00-00-001.mp4");
            Assert.Contains("\"count\":1", deleted);
            Assert.False(File.Exists(file));

            // 越界路径必须失败
            var blocked = await client.GetStringAsync(
                $"{host.Address}/index/api/deleteRecordFile?path=../outside.txt");
            Assert.Contains("\"count\":0", blocked);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
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

            await Task.Delay(100, cancellationToken);
        }

        return false;
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
        catch (UnauthorizedAccessException)
        {
        }
    }
}
