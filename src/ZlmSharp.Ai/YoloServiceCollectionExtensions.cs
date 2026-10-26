using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;
using ZlmSharp.Core;

namespace ZlmSharp.Ai;

public static class YoloServiceCollectionExtensions
{
    /// <summary>
    /// 注册 YOLO 推理服务（模型懒加载；未配置 <c>ZLM_YOLO_MODEL</c> 时 <see cref="YoloDetectorService.Enabled"/> 为 false）、
    /// 定时检测后台任务 <see cref="StreamYoloMonitor"/>，以及结果叠加流管理器 <see cref="YoloOverlayManager"/>。
    /// </summary>
    public static IServiceCollection AddZlmSharpYolo(this IServiceCollection services, EnvOptions env)
    {
        var options = YoloOptions.FromEnv(env);
        services.AddSingleton(options);
        services.AddSingleton<YoloResultStore>();
        services.AddSingleton(provider => new YoloDetectorService(
            options,
            provider.GetService<ILoggerFactory>()?.CreateLogger<YoloDetectorService>()));
        services.AddSingleton(provider => new YoloOverlayManager(
            provider.GetRequiredService<MediaSourceRegistry>(),
            provider.GetRequiredService<YoloResultStore>(),
            provider.GetRequiredService<AppOptions>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<YoloOverlayManager>()));
        services.AddHostedService<StreamYoloMonitor>();
        return services;
    }
}
