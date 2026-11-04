using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Codecs;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>
/// 录像 API：路径归一化（不返回文件系统绝对路径）、getRecordList、/record 文件访问。
/// </summary>
public class RecordApiTests
{
    [Fact]
    public async Task StartRecord_ReturnsRelativePath_AndServesFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "zlmsharp-record-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var app = await StartHostAsync(root);

        try
        {
            using var client = new HttpClient();
            var address = GetAddress(app);

            var registry = app.Services.GetRequiredService<MediaSourceRegistry>();
            var source = registry.GetOrCreate(StreamKey.Create(null, "live", "cam"), "rtmp");
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBConfig(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Config | FrameFlags.Key));
            source.AddFrame(MediaFrame.Create(
                TestData.AnnexBKeyFrame(), TrackType.Video, CodecId.H264, 0, 0, FrameFlags.Key));

            using var start = JsonDocument.Parse(
                await client.GetStringAsync($"{address}/index/api/startRecord?app=live&stream=cam&type=3"));
            Assert.Equal(0, start.RootElement.GetProperty("code").GetInt32());

            var data = start.RootElement.GetProperty("data");
            var mp4Path = data.GetProperty("mp4Path").GetString();
            var flvPath = data.GetProperty("flvPath").GetString();

            Assert.False(string.IsNullOrEmpty(mp4Path));
            Assert.False(string.IsNullOrEmpty(flvPath));

            // 归一化：不含盘符与根前缀，统一 '/' 分隔
            Assert.False(Path.IsPathRooted(mp4Path));
            Assert.DoesNotContain('\\', mp4Path!);
            Assert.StartsWith("live/cam/", mp4Path);
            Assert.EndsWith(".mp4", mp4Path);
            Assert.True(File.Exists(Path.Combine(root, mp4Path)));

            // 未启用 HLS 时 hlsPath 为空
            Assert.Equal(JsonValueKind.Null, data.GetProperty("hlsPath").ValueKind);

            // getRecordList 能看到这一路
            using var list = JsonDocument.Parse(
                await client.GetStringAsync($"{address}/index/api/getRecordList?app=live&stream=cam"));
            Assert.Equal(0, list.RootElement.GetProperty("code").GetInt32());
            var items = list.RootElement.GetProperty("data");
            Assert.Equal(1, items.GetArrayLength());
            Assert.Equal(mp4Path, items[0].GetProperty("mp4Path").GetString());
            Assert.Equal("recording", items[0].GetProperty("state").GetString());

            // /record/{path} 能访问到同一文件
            using var response = await client.GetAsync($"{address}/record/{mp4Path}");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);

            // 目录穿越必须被拒绝
            using var escaped = await client.GetAsync($"{address}/record/..%2f..%2fetc%2fpasswd");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, escaped.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task GetRecordFile_ReturnsRelativeRootPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "zlmsharp-record-" + Guid.NewGuid().ToString("N"));
        var day = Path.Combine(root, "live", "cam", "2026-11-04");
        Directory.CreateDirectory(day);
        await File.WriteAllBytesAsync(Path.Combine(day, "20-00-00-001.mp4"), [0x00, 0x01]);

        var app = await StartHostAsync(root);

        try
        {
            using var client = new HttpClient();
            var address = GetAddress(app);

            // 根目录浏览（前端进入页面时的默认视图）：列出 app 目录
            using var rootList = JsonDocument.Parse(
                await client.GetStringAsync($"{address}/index/api/getMp4RecordFile"));
            var rootData = rootList.RootElement.GetProperty("data");
            Assert.Equal(string.Empty, rootData.GetProperty("rootPath").GetString());
            Assert.True(rootData.GetProperty("directories").GetBoolean());
            Assert.Contains("live", rootData.GetProperty("paths").EnumerateArray().Select(item => item.GetString()));

            // /record/live 下还有 cam 子目录：必须返回子目录而不是「没有文件」
            using var appLevel = JsonDocument.Parse(
                await client.GetStringAsync($"{address}/index/api/getMp4RecordFile?path=live"));
            var appData = appLevel.RootElement.GetProperty("data");
            Assert.Equal("live", appData.GetProperty("rootPath").GetString());
            Assert.True(appData.GetProperty("directories").GetBoolean());
            Assert.Equal(["cam"], appData.GetProperty("paths").EnumerateArray().Select(item => item.GetString()));

            // 只给 app 也能定位到 live 目录
            using var byApp = JsonDocument.Parse(
                await client.GetStringAsync($"{address}/index/api/getMp4RecordFile?app=live"));
            Assert.Equal("live", byApp.RootElement.GetProperty("data").GetProperty("rootPath").GetString());

            // 根目录浏览：返回日期目录
            using var top = JsonDocument.Parse(
                await client.GetStringAsync($"{address}/index/api/getMp4RecordFile?app=live&stream=cam"));
            Assert.Equal("live/cam", top.RootElement.GetProperty("data").GetProperty("rootPath").GetString());
            Assert.True(top.RootElement.GetProperty("data").GetProperty("directories").GetBoolean());

            // 进入日期目录：返回文件名，rootPath 是相对目录
            using var inner = JsonDocument.Parse(
                await client.GetStringAsync($"{address}/index/api/getMp4RecordFile?path={Uri.EscapeDataString("live/cam/2026-11-04")}"));
            var data = inner.RootElement.GetProperty("data");
            Assert.Equal("live/cam/2026-11-04", data.GetProperty("rootPath").GetString());
            Assert.False(data.GetProperty("directories").GetBoolean());
            var entries = data.GetProperty("paths");
            Assert.Equal(1, entries.GetArrayLength());
            Assert.Equal("20-00-00-001.mp4", entries[0].GetString());

            using var file = await client.GetAsync($"{address}/record/live/cam/2026-11-04/20-00-00-001.mp4");
            Assert.Equal(System.Net.HttpStatusCode.OK, file.StatusCode);
            Assert.Equal(2, (await file.Content.ReadAsByteArrayAsync()).Length);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<WebApplication> StartHostAsync(string recordRoot)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var options = new AppOptions();
        options.Http.AllowOrigins = "*";
        options.Rtmp.Port = 0;
        options.Rtsp.Port = 0;
        options.Protocol.Mp4SavePath = recordRoot;

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
