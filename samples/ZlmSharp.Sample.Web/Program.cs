using ZlmSharp.Server;

// 演示：把 zlmsharp 作为库嵌进一个普通的 ASP.NET Core 应用（app.Use 方式）。
// 独立运行模式见 src/ZlmSharp.Cli（自建 Kestrel + 相同的一套 AddZlmSharp/UseZlmSharp）。
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddZlmSharp(options =>
{
    // 不指定 ConfigPath/EnvPath 时，缺省读 exe 同目录的 zlmsharp.ini 与 .env（不存在自动生成）
    options.ConfigureLogging = false;      // 沿用宿主日志体系（NLog/Serilog/默认控制台都行）
    options.RegisterStreamListeners = false; // 端口由宿主统一管理，避免和宿主进程冲突
});

var app = builder.Build();

app.MapGet("/", () => "hello from host app");

// 挂载 zlmsharp 的 /index/api/*、后续的 HTTP-FLV/WS-FLV 端点
app.UseZlmSharp();

app.Run("http://127.0.0.1:5180");
