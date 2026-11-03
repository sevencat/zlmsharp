using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZlmSharp.Ai;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Configuration;
using ZlmSharp.Extra.Analysis;
using ZlmSharp.Extra.Capture;
using ZlmSharp.Extra.Overlay;
using ZlmSharp.Extra.Yolo;

namespace ZlmSharp.Extra;

/// <summary>
/// 注册扩展包：内置 YOLO 分析器 + 取帧/叠加管线 + 分析调度。
/// 需要在 <c>AddZlmSharp</c> 之后调用；不调用则所有 AI 相关 API 按“未接入”处理。
/// </summary>
public static class ExtraServiceCollectionExtensions
{
    public static IServiceCollection AddZlmSharpExtra(this IServiceCollection services, Action<IExtraBuilder>? configure = null)
    {
        var builder = new ExtraBuilder(services);

        // 内置分析器：YOLO（可查快照 + 可叠加）
        builder.AddAnalyzer<YoloAnalyzer>(options =>
            options.Outputs = AnalysisOutputs.Snapshot | AnalysisOutputs.Overlay);

        configure?.Invoke(builder);

        services.TryAddSingleton<IYoloBackendFactory>(UnavailableYoloBackendFactory.Instance);
        services.TryAddSingleton(provider => YoloOptions.FromEnv(provider.GetRequiredService<AppOptions>().Env));
        services.TryAddSingleton<YoloDetectorService>();
        services.TryAddSingleton(provider => new FfmpegDecoderPool(
            provider.GetRequiredService<AppOptions>().Env.ResolveFfmpegPath(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<FfmpegDecoderPool>()));
        services.TryAddSingleton<OverlayResultCache>();
        services.TryAddSingleton<OverlayComposer>(OverlayComposer.Default());
        services.TryAddSingleton<YoloOverlayManager>();
        services.TryAddSingleton<AnalysisSinks>();
        services.TryAddSingleton<AnalysisHost>();
        services.TryAddSingleton<IAnalyzerCatalog>(provider => provider.GetRequiredService<AnalysisHost>());
        services.TryAddSingleton<IImageAnalysisService>(provider => provider.GetRequiredService<AnalysisHost>());
        services.TryAddSingleton<IOverlayController>(provider => provider.GetRequiredService<YoloOverlayManager>());

        // 注册顺序即启动顺序：先填注册表，再启动分析调度
        services.AddHostedService(provider => new ExtraInitializer(
            provider,
            builder.AnalyzerFactories,
            builder.SinkFactories));
        services.AddHostedService<AnalysisScheduler>();
        return services;
    }
}

/// <summary>宿主启动时把分析器 / sink 装进注册表（在分析调度之前执行）。</summary>
internal sealed class ExtraInitializer(
    IServiceProvider provider,
    IReadOnlyList<Func<IServiceProvider, AnalyzerRegistration>> analyzerFactories,
    IReadOnlyList<Func<IServiceProvider, IAnalysisSink>> sinkFactories) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var registry = provider.GetRequiredService<AnalyzerRegistry>();
        foreach (var factory in analyzerFactories)
        {
            registry.Register(factory(provider));
        }

        var sinks = provider.GetRequiredService<AnalysisSinks>();
        foreach (var factory in sinkFactories)
        {
            sinks.Add(factory(provider));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
