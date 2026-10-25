using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;
using ZlmSharp.Server;

namespace ZlmSharp.Cli;

internal static class CliApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        TrySetUtf8Console();

        CliArguments cli;
        try
        {
            cli = CliArguments.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        if (cli.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (cli.ShowVersion)
        {
            Console.WriteLine($"zlmsharp {ServerInfo.Version} (build {ServerInfo.BuildTime}, {ServerInfo.BranchName}/{ServerInfo.CommitHash})");
            return 0;
        }

        ConfigLoadResult config;
        try
        {
            config = ConfigLoader.Load(cli.ConfigPath, cli.EnvPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"加载配置失败: {ex.Message}");
            return 1;
        }

        if (config.Created)
        {
            Console.WriteLine($"未找到配置文件，已生成默认配置: {config.ConfigPath}");
        }

        if (config.EnvCreated)
        {
            Console.WriteLine($"未找到 .env，已生成模板: {config.EnvPath}");
        }

        WebApplication app;
        try
        {
            app = BuildHost(config);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"初始化失败: {ex.Message}");
            return 1;
        }

        await using (app.ConfigureAwait(false))
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ZlmSharp");
            LogStartup(logger, config, cli);

            try
            {
                await app.RunAsync().ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                logger.LogCritical(
                    ex,
                    "启动失败：HTTP 端口 {Port} 无法监听（被占用或权限不足；Windows 非管理员请改用 >=1024 端口）",
                    config.Options.Http.Port);
                return 3;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "服务异常退出");
                return 1;
            }
        }
    }

    /// <summary>独立运行模式：自建精简宿主，复用库中的 AddZlmSharp/UseZlmSharp。</summary>
    private static WebApplication BuildHost(ConfigLoadResult config)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = "ZlmSharp",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });

        // 不读 appsettings.json：配置全部来自 zlmsharp.ini + .env
        builder.Configuration.Sources.Clear();

        builder.Services.AddZlmSharp(options =>
        {
            options.Options = config.Options;
            options.Document = config.Document;
            options.DotEnv = config.DotEnv;
            options.ConfigureLogging = true;
            options.ClearOtherLoggingProviders = true;
            options.RegisterStreamListeners = true;
        });

        ZlmSharpKestrel.Configure(builder, config.Options);

        var app = builder.Build();
        app.UseZlmSharp();
        return app;
    }

    private static void LogStartup(ILogger logger, ConfigLoadResult config, CliArguments cli)
    {
        var options = config.Options;

        logger.LogInformation(
            "zlmsharp {Version} 启动（build {BuildTime}, {Branch}/{Commit}）",
            ServerInfo.Version,
            ServerInfo.BuildTime,
            ServerInfo.BranchName,
            ServerInfo.CommitHash);
        logger.LogInformation("配置文件: {ConfigPath}", config.ConfigPath);
        logger.LogInformation("环境配置: {EnvPath}", config.EnvPath);

        foreach (var warning in options.Warnings)
        {
            logger.LogWarning("配置: {Warning}", warning);
        }

        logger.LogInformation("ffmpeg: {FfmpegPath}", options.Env.ResolveFfmpegPath());

        if (options.Http.Port > 0)
        {
            logger.LogInformation("HTTP/API: http://{Host}:{Port}/index/api/version", NormalizeHost(options.General.ListenIp), options.Http.Port);
        }
        else
        {
            logger.LogWarning("HTTP/API 未启用（http.port=0）");
        }

        if (options.Rtsp.Port > 0)
        {
            logger.LogInformation("RTSP 端口: {Port}", options.Rtsp.Port);
        }

        if (options.Rtmp.Port > 0)
        {
            logger.LogInformation("RTMP 端口: {Port}", options.Rtmp.Port);
        }

        if (cli.Daemon)
        {
            logger.LogInformation("-d 已接受，但当前版本仍以前台方式运行（守护进程/服务化在后续版本提供）");
        }
    }

    private static string NormalizeHost(string listenIp) =>
        string.IsNullOrWhiteSpace(listenIp) || listenIp is "::" or "0.0.0.0" or "*"
            ? "127.0.0.1"
            : listenIp;

    private static void PrintUsage()
    {
        Console.WriteLine("""
            用法: zlmsharp [选项]

              -c, --conf <path>   指定 zlmsharp.ini（缺省为 exe 同目录，不存在时自动生成）
              -e, --env  <path>   指定 .env（缺省为 exe 同目录，不存在时自动生成模板）
              -d, --daemon        守护进程模式（当前版本仍为前台运行，仅为兼容保留）
              -v, --version       显示版本信息
              -h, --help          显示帮助

            示例:
              zlmsharp
              zlmsharp -c ./conf/zlmsharp.ini -e ./conf/.env
            """);
    }

    private static void TrySetUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 某些重定向/无控制台环境下不支持，忽略
        }
    }
}
