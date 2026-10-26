using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using ZlmSharp.Ai;
using ZlmSharp.Api;
using ZlmSharp.Configuration;
using ZlmSharp.Http;
using ZlmSharp.Server.Net;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ZlmSharp.Server;

/// <summary>
/// 两种用法：
/// 1. 独立服务：CLI 里 <c>CreateSlimBuilder</c> + <c>AddZlmSharp</c> + <c>UseZlmSharp</c>；
/// 2. 嵌入已有 ASP.NET Core 应用：<c>builder.Services.AddZlmSharp(...)</c> + <c>app.UseZlmSharp()</c>。
/// </summary>
public static class ZlmSharpHostingExtensions
{
    public static IServiceCollection AddZlmSharp(this IServiceCollection services, Action<ZlmSharpOptions>? configure = null)
    {
        var settings = new ZlmSharpOptions();
        configure?.Invoke(settings);

        var runtime = ZlmSharpRuntime.Create(settings);

        services.AddSingleton(runtime);
        services.AddSingleton(runtime.Options);
        services.AddSingleton(runtime.Document);
        services.AddSingleton(runtime.DotEnv);
        services.AddSingleton<ZlmSharp.Core.MediaSourceRegistry>();
        services.AddZlmSharpYolo(runtime.Options.Env);

        if (runtime.NLogConfiguration is { } nlogConfiguration)
        {
            services.AddLogging(logging =>
            {
                if (settings.ClearOtherLoggingProviders)
                {
                    logging.ClearProviders();
                }

                logging.AddNLog(nlogConfiguration);
                logging.SetMinimumLevel(MsLogLevel.Trace);
            });
        }

        if (settings.RegisterStreamListeners)
        {
            if (runtime.Options.Rtsp.Port > 0)
            {
                services.AddSingleton<IHostedService>(provider =>
                    new RtspListenerService(
                        runtime.Options,
                        provider.GetRequiredService<ZlmSharp.Core.MediaSourceRegistry>(),
                        provider.GetRequiredService<ILoggerFactory>()));
            }

            if (runtime.Options.Rtmp.Port > 0)
            {
                services.AddSingleton<IHostedService>(provider =>
                    new RtmpListenerService(
                        runtime.Options,
                        provider.GetRequiredService<ZlmSharp.Core.MediaSourceRegistry>(),
                        provider.GetRequiredService<ILoggerFactory>()));
            }
        }

        return services;
    }

    /// <summary>把 HTTP-FLV/WS-FLV/API 端点挂到宿主（需要端点路由，WebApplication 满足）。</summary>
    public static IApplicationBuilder UseZlmSharp(this IApplicationBuilder app)
    {
        if (app is not IEndpointRouteBuilder endpoints)
        {
            throw new InvalidOperationException("UseZlmSharp 需要端点路由：请使用 WebApplication，或在 UseEndpoints 内调用。");
        }

        // WS-FLV 需要 WebSocket 中间件；重复注册无害
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        endpoints.MapZlmSharp();
        return app;
    }

    /// <summary>在 <c>UseEndpoints</c> 内挂载端点。</summary>
    public static IEndpointRouteBuilder MapZlmSharp(this IEndpointRouteBuilder endpoints)
    {
        var runtime = endpoints.ServiceProvider.GetRequiredService<ZlmSharpRuntime>();
        ApiEndpoints.Map(
            endpoints,
            runtime.Options,
            new ApiVersionInfo(ServerInfo.BuildTime, ServerInfo.BranchName, ServerInfo.CommitHash));

        HttpFlvEndpoint.Map(endpoints);
        return endpoints;
    }
}
