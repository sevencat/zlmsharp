using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;
using ZlmSharp.Core;

namespace ZlmSharp.Recording;

public static class RecordingServiceCollectionExtensions
{
    /// <summary>注册录像管理器与自动录像巡检（需先注册 <see cref="MediaSourceRegistry"/> 与 <see cref="AppOptions"/>）。</summary>
    public static IServiceCollection AddZlmSharpRecording(this IServiceCollection services)
    {
        services.AddSingleton(provider => new RecorderManager(
            provider.GetRequiredService<MediaSourceRegistry>(),
            provider.GetRequiredService<AppOptions>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<RecorderManager>()));

        services.AddHostedService(provider => new RecorderAutoStartService(
            provider.GetRequiredService<RecorderManager>(),
            provider.GetRequiredService<MediaSourceRegistry>(),
            provider.GetRequiredService<AppOptions>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<RecorderAutoStartService>()));

        return services;
    }
}
