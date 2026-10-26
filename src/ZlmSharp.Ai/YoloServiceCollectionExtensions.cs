using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;

namespace ZlmSharp.Ai;

public static class YoloServiceCollectionExtensions
{
    /// <summary>注册 YOLO 推理服务（模型懒加载；未配置 <c>ZLM_YOLO_MODEL</c> 时 <see cref="YoloDetectorService.Enabled"/> 为 false）。</summary>
    public static IServiceCollection AddZlmSharpYolo(this IServiceCollection services, EnvOptions env)
    {
        var options = YoloOptions.FromEnv(env);
        services.AddSingleton(options);
        services.AddSingleton(provider => new YoloDetectorService(
            options,
            provider.GetService<ILoggerFactory>()?.CreateLogger<YoloDetectorService>()));
        return services;
    }
}
