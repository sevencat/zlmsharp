using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using ZlmSharp.Api;
using ZlmSharp.Configuration;
using ZlmSharp.Http;
using ZlmSharp.Recording;
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
        // 管理页面授权：默认按 [api] pageToken 校验；宿主可注册自己的 IAdminPageAuthorizer 覆盖
        services.TryAddSingleton<ZlmSharp.Server.IAdminPageAuthorizer, ZlmSharp.Server.StaticTokenPageAuthorizer>();
        services.AddSingleton(provider => new ZlmSharp.Core.MediaSourceRegistry(
            provider.GetService<ILoggerFactory>()?.CreateLogger("ZlmSharp.Media"),
            new ZlmSharp.Core.MediaSourceOptions
            {
                GopCacheMs = runtime.Options.General.GopCacheMS,
                CatchUpPendingFrames = runtime.Options.General.CatchUpPendingFrames,
            }));
        services.AddSingleton(provider => new ZlmSharp.Core.MediaSessionRegistry(
            provider.GetService<ILoggerFactory>()?.CreateLogger("ZlmSharp.Session")));
        services.AddSingleton<Hooks.MediaHookService>();
        services.AddSingleton<ZlmSharp.Core.MediaHookHandler>(provider =>
            provider.GetRequiredService<Hooks.MediaHookService>().InvokeAsync);
        // AI 分析：核心库只注册契约容器（结果存储/分析器注册表），具体实现由 ZlmSharp.Extra
        // 的 AddZlmSharpExtra 接入；不接入时相关 API 按“未启用”处理。
        services.TryAddSingleton<ZlmSharp.Ai.Analysis.AnalysisResultStore>();
        services.TryAddSingleton<ZlmSharp.Ai.Analysis.AnalyzerRegistry>();
        services.AddZlmSharpRecording();
        services.AddSingleton(provider =>
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("ZlmSharp.Proxy");
            return new ZlmSharp.Protocols.Rtmp.RtmpPullProxyManager(
                provider.GetRequiredService<ZlmSharp.Core.MediaSourceRegistry>(),
                (level, message) => logger.Log(
                    level switch
                    {
                        ZlmSharp.Protocols.Rtmp.RtmpLogLevel.Debug => MsLogLevel.Debug,
                        ZlmSharp.Protocols.Rtmp.RtmpLogLevel.Info => MsLogLevel.Information,
                        ZlmSharp.Protocols.Rtmp.RtmpLogLevel.Warning => MsLogLevel.Warning,
                        _ => MsLogLevel.Error,
                    },
                    "{Message}",
                    message),
                runtime.Options.Proxy.MaxConcurrency,
                runtime.Options.Proxy.RetryDelayMS,
                (level, message) => logger.Log(
                    level switch
                    {
                        ZlmSharp.Protocols.Rtsp.RtspLogLevel.Debug => MsLogLevel.Debug,
                        ZlmSharp.Protocols.Rtsp.RtspLogLevel.Info => MsLogLevel.Information,
                        ZlmSharp.Protocols.Rtsp.RtspLogLevel.Warning => MsLogLevel.Warning,
                        _ => MsLogLevel.Error,
                    },
                    "{Message}",
                    message));
        });
        services.AddSingleton(provider =>
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("ZlmSharp.Push");
            return new ZlmSharp.Protocols.Rtmp.RtmpPushManager(
                provider.GetRequiredService<ZlmSharp.Core.MediaSourceRegistry>(),
                (level, message) => logger.Log(
                    level switch
                    {
                        ZlmSharp.Protocols.Rtmp.RtmpLogLevel.Debug => MsLogLevel.Debug,
                        ZlmSharp.Protocols.Rtmp.RtmpLogLevel.Info => MsLogLevel.Information,
                        ZlmSharp.Protocols.Rtmp.RtmpLogLevel.Warning => MsLogLevel.Warning,
                        _ => MsLogLevel.Error,
                    },
                    "{Message}",
                    message),
                new ZlmSharp.Protocols.Rtmp.RtmpPushOptions
                {
                    Enhanced = runtime.Options.Rtmp.Enhanced,
                });
        });

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

        if (runtime.Options.Proxy.Enable)
        {
            services.AddSingleton<IHostedService>(provider =>
                new ProxyAutoStartService(
                    runtime.Options,
                    provider.GetRequiredService<ZlmSharp.Protocols.Rtmp.RtmpPullProxyManager>(),
                    provider.GetRequiredService<ILoggerFactory>()));
        }

        // 按需拉流：<c>[proxy] on_demand_list</c> 预置，或运行期 addStreamProxy?on_demand=1 动态登记；
        // 两种都要，所以无条件注册（列表为空时只空转 1s tick）。
        services.AddSingleton(provider => new OnDemandProxyService(
            runtime.Options.Proxy,
            provider.GetRequiredService<ZlmSharp.Core.MediaSourceRegistry>(),
            provider.GetRequiredService<ZlmSharp.Protocols.Rtmp.RtmpPullProxyManager>(),
            provider.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<ZlmSharp.Core.IOnDemandProxy>(provider =>
            provider.GetRequiredService<OnDemandProxyService>());
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<OnDemandProxyService>());

        if (runtime.Options.General.StreamNoneReaderDelayMS > 0)
        {
            services.AddSingleton<IHostedService>(provider =>
                new IdleStreamCleanupService(
                    runtime.Options,
                    provider.GetRequiredService<ZlmSharp.Core.MediaSourceRegistry>(),
                    provider.GetRequiredService<ZlmSharp.Core.MediaSessionRegistry>(),
                    provider.GetRequiredService<Hooks.MediaHookService>(),
                    provider.GetRequiredService<ILoggerFactory>(),
                    provider.GetService<ZlmSharp.Ai.IOverlayController>(),
                    provider.GetService<ZlmSharp.Recording.RecorderManager>(),
                    provider.GetService<ZlmSharp.Protocols.Rtmp.RtmpPullProxyManager>(),
                    provider.GetService<ZlmSharp.Core.IOnDemandProxy>()));
        }

        if (settings.RegisterStreamListeners)
        {
            if (runtime.Options.Rtsp.Port > 0)
            {
                services.AddSingleton<IHostedService>(provider =>
                    new RtspListenerService(
                        runtime.Options,
                        provider.GetRequiredService<ZlmSharp.Core.MediaSourceRegistry>(),
                        provider.GetRequiredService<ILoggerFactory>(),
                        provider.GetRequiredService<ZlmSharp.Core.MediaSessionRegistry>(),
                        provider.GetRequiredService<ZlmSharp.Core.MediaHookHandler>(),
                        provider.GetService<ZlmSharp.Core.IOnDemandProxy>()));
            }

            if (runtime.Options.Rtmp.Port > 0)
            {
                services.AddSingleton<IHostedService>(provider =>
                    new RtmpListenerService(
                        runtime.Options,
                        provider.GetRequiredService<ZlmSharp.Core.MediaSourceRegistry>(),
                        provider.GetRequiredService<ILoggerFactory>(),
                        provider.GetRequiredService<ZlmSharp.Core.MediaSessionRegistry>(),
                        provider.GetRequiredService<ZlmSharp.Core.MediaHookHandler>(),
                        provider.GetService<ZlmSharp.Core.IOnDemandProxy>()));
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
        AdminPageEndpoints.Map(endpoints);
        return endpoints;
    }
}
