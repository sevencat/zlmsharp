using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Configuration;
using ZlmSharp.Core;

namespace ZlmSharp.Ai;

public static class YoloServiceCollectionExtensions
{
    /// <summary>
    /// 注册 YOLO 推理服务（模型懒加载；未配置 <c>ZLM_YOLO_MODEL</c> 时 <see cref="YoloDetectorService.Enabled"/> 为 false）、
    /// 定时检测后台任务 <see cref="StreamYoloMonitor"/>，以及结果叠加流管理器 <see cref="YoloOverlayManager"/>。
    /// </summary>
    /// <remarks>
    /// 推理引擎不在核心库里：需要在调用本方法前后注册自己的 <see cref="IYoloBackendFactory"/>
    /// （例如 SimdYolo 适配器）。不注册时退化为"无后端"，检测入口按未启用处理。
    /// </remarks>
    public static IServiceCollection AddZlmSharpYolo(this IServiceCollection services, EnvOptions env)
    {
        var options = YoloOptions.FromEnv(env);
        services.TryAddSingleton<IYoloBackendFactory>(UnavailableYoloBackendFactory.Instance);
        services.AddSingleton(options);
        services.AddSingleton<YoloResultStore>();
        services.AddSingleton<AnalysisResultStore>();
        services.AddSingleton<AnalyzerRegistry>(provider =>
        {
            var registry = new AnalyzerRegistry();
            registry.Register(provider.GetRequiredService<YoloAnalyzer>());
            return registry;
        });
        services.AddSingleton(provider => new YoloAnalyzer(
            provider.GetRequiredService<YoloDetectorService>()));
        services.AddSingleton(provider => new YoloDetectorService(
            options,
            provider.GetService<IYoloBackendFactory>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<YoloDetectorService>()));
        services.AddSingleton(provider => new YoloOverlayManager(
            provider.GetRequiredService<MediaSourceRegistry>(),
            provider.GetRequiredService<YoloResultStore>(),
            provider.GetRequiredService<AppOptions>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<YoloOverlayManager>()));
        services.AddSingleton(provider => new FfmpegDecoderPool(
            env.ResolveFfmpegPath(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<FfmpegDecoderPool>()));
        services.AddHostedService<StreamYoloMonitor>();
        return services;
    }
}
