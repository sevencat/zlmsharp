using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZlmSharp.Configuration;
using ZlmSharp.Extra.Media;

namespace ZlmSharp.Media.AutoGen;

/// <summary>把媒体实现替换为进程内 libav（FFmpeg.AutoGen 8.x dll）。</summary>
public static class AutoGenServiceCollectionExtensions
{
    /// <summary>
    /// 用 <see cref="AutoGenMediaToolkit"/> 替换已有的 <see cref="IMediaToolkit"/> 注册。
    /// dll 目录默认取 <see cref="AppOptions"/> 里 <c>ZLM_FFMPEG_PATH</c> 指向的 ffmpeg.exe 所在目录。
    /// </summary>
    public static IServiceCollection AddAutoGenMediaToolkit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IMediaToolkit>(provider =>
        {
            var environment = provider.GetRequiredService<AppOptions>().Env;
            return new AutoGenMediaToolkit(environment.ResolveFfmpegPath());
        }));

        return services;
    }

    /// <summary>用 <see cref="AutoGenMediaToolkit"/> 替换，并显式指定 ffmpeg.exe 路径或 dll 目录。</summary>
    public static IServiceCollection AddAutoGenMediaToolkit(
        this IServiceCollection services,
        string? ffmpegPathOrDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IMediaToolkit>(
            _ => new AutoGenMediaToolkit(ffmpegPathOrDirectory)));

        return services;
    }
}
