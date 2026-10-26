using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ZlmSharp.Configuration;
using ZlmSharp.Server;

namespace ZlmSharp.Tests;

/// <summary>YOLO API：未配置模型 / 模型不可读 / PPM 入参三条路径（不做真实推理，避免依赖模型文件）。</summary>
public class YoloApiTests
{
    [Fact]
    public async Task YoloInfo_WithoutModel_ReportsNotEnabled()
    {
        var app = await StartHostAsync(null);

        try
        {
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync($"{GetAddress(app)}/index/api/yoloInfo"));
            var root = document.RootElement;

            Assert.Equal(-1, root.GetProperty("code").GetInt32());
            Assert.Contains("未启用", root.GetProperty("msg").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task YoloDetect_WithoutModel_ReportsNotEnabled()
    {
        var app = await StartHostAsync(null);

        try
        {
            using var client = new HttpClient();
            using var content = new ByteArrayContent(BuildPpm(1, 1));
            using var response = await client.PostAsync($"{GetAddress(app)}/index/api/yoloDetect", content);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(-1, document.RootElement.GetProperty("code").GetInt32());
            Assert.Contains("未启用", document.RootElement.GetProperty("msg").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task YoloInfo_WithUnreadableModel_ReportsLoadError()
    {
        var app = await StartHostAsync("no_such_yolo_model.pt");

        try
        {
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync($"{GetAddress(app)}/index/api/yoloInfo"));
            var root = document.RootElement;

            Assert.Equal(-1, root.GetProperty("code").GetInt32());
            Assert.Contains("无法读取", root.GetProperty("msg").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task YoloDetect_WithPpmBody_TriesModelLoad()
    {
        var app = await StartHostAsync("no_such_yolo_model.pt");

        try
        {
            using var client = new HttpClient();
            using var content = new ByteArrayContent(BuildPpm(4, 3));
            using var response = await client.PostAsync($"{GetAddress(app)}/index/api/yoloDetect", content);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;

            // PPM 解析通过后才会去加载模型，因此这里应是“加载失败”而不是“未启用/请提供图片”
            Assert.Equal(-1, root.GetProperty("code").GetInt32());
            Assert.Contains("加载失败", root.GetProperty("msg").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task YoloDetect_InvalidPpm_ReportsParseError()
    {
        var app = await StartHostAsync("no_such_yolo_model.pt");

        try
        {
            using var client = new HttpClient();
            using var content = new ByteArrayContent("P3\n1 1\n255\n0 0 0"u8.ToArray());
            using var response = await client.PostAsync($"{GetAddress(app)}/index/api/yoloDetect", content);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;

            Assert.Equal(-1, root.GetProperty("code").GetInt32());
            Assert.Contains("P6", root.GetProperty("msg").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task YoloInfo_WithModelDirOnly_ListsModels()
    {
        var dir = CreateModelDir();
        var app = await StartHostAsync(null, dir);

        try
        {
            using var client = new HttpClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync($"{GetAddress(app)}/index/api/yoloInfo"));
            var root = document.RootElement;

            Assert.Equal(0, root.GetProperty("code").GetInt32());

            var data = root.GetProperty("data");
            Assert.False(data.GetProperty("enabled").GetBoolean());
            Assert.Equal(dir, data.GetProperty("modelDir").GetString());

            var models = data.GetProperty("models").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("dummy.onnx", models);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task YoloDetect_ModelSelection_ReportsInvalidNames()
    {
        var dir = CreateModelDir();
        var app = await StartHostAsync("dummy.onnx", dir);

        try
        {
            using var client = new HttpClient();

            using var missing = await PostDetectAsync(client, app, "missing.onnx");
            using var missingDocument = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
            Assert.Equal(-1, missingDocument.RootElement.GetProperty("code").GetInt32());
            Assert.Contains("模型不存在", missingDocument.RootElement.GetProperty("msg").GetString());

            using var traversal = await PostDetectAsync(client, app, "../dummy.onnx");
            using var traversalDocument = JsonDocument.Parse(await traversal.Content.ReadAsStringAsync());
            Assert.Equal(-1, traversalDocument.RootElement.GetProperty("code").GetInt32());
            Assert.Contains("不能包含路径", traversalDocument.RootElement.GetProperty("msg").GetString());

            using var badExtension = await PostDetectAsync(client, app, "dummy.txt");
            using var badExtensionDocument = JsonDocument.Parse(await badExtension.Content.ReadAsStringAsync());
            Assert.Contains("扩展名", badExtensionDocument.RootElement.GetProperty("msg").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Task<HttpResponseMessage> PostDetectAsync(HttpClient client, WebApplication app, string model)
    {
        var content = new ByteArrayContent(BuildPpm(2, 2));
        return client.PostAsync($"{GetAddress(app)}/index/api/yoloDetect?model={Uri.EscapeDataString(model)}", content);
    }

    private static string CreateModelDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zlm-yolo-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "dummy.onnx"), [0x00, 0x01, 0x02]);
        return dir;
    }

    private static async Task<WebApplication> StartHostAsync(string? yoloModel, string? yoloModelDir = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var options = new AppOptions();
        options.Http.AllowOrigins = "*";
        options.Env.YoloModel = yoloModel ?? string.Empty;
        options.Env.YoloModelDir = yoloModelDir ?? string.Empty;

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

    private static byte[] BuildPpm(int width, int height)
    {
        var header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        var data = new byte[header.Length + (width * height * 3)];
        header.CopyTo(data, 0);
        return data;
    }

    private static string GetAddress(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses;

        return addresses.First().Replace("[::]", "127.0.0.1").TrimEnd('/');
    }
}
