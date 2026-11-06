using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZlmSharp.Ai;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Extra.Analysis;
using ZlmSharp.Extra.Capture;
using ZlmSharp.Extra.Media;
using ZlmSharp.Extra.Ocr;
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

        // 内置分析器：YOLO（目标检测）+ OCR（文字识别），都可查快照 + 可叠加
        builder.AddAnalyzer<YoloAnalyzer>(options =>
            options.Outputs = AnalysisOutputs.Snapshot | AnalysisOutputs.Overlay);
        builder.AddAnalyzer<OcrAnalyzer>(options =>
            options.Outputs = AnalysisOutputs.Snapshot | AnalysisOutputs.Overlay);

        configure?.Invoke(builder);

        // 推理后端：SimdYolo 源码存在时启用（SIMDYOLO），否则 IsAvailable=false，检测入口按未启用处理
        services.TryAddSingleton<IYoloBackendFactory>(UnavailableYoloBackendFactory.Instance);
        services.Replace(ServiceDescriptor.Singleton<IYoloBackendFactory, SimdYoloBackendFactory>());
        services.TryAddSingleton(provider => YoloOptions.FromEnv(provider.GetRequiredService<AppOptions>().Env));
        services.TryAddSingleton<YoloDetectorService>();

        // OCR：模型内置在 NuGet 包（tiny），也可用自定义 DET/REC/DICT
        services.TryAddSingleton(provider => OcrOptions.FromEnv(provider.GetRequiredService<AppOptions>().Env));
        services.TryAddSingleton(provider => new OcrEngineService(
            provider.GetRequiredService<OcrOptions>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<OcrEngineService>()));

        // 外部工具（ffmpeg/ffprobe）：全部媒体操作都走这个入口，方便换成 DLL 实现
        services.TryAddSingleton<IMediaToolkit>(provider =>
        {
            var env = provider.GetRequiredService<AppOptions>().Env;
            return new FfmpegToolkit(env.ResolveFfmpegPath(), env.ResolveFfprobePath());
        });
        services.TryAddSingleton(provider => new FfmpegDecoderPool(
            provider.GetRequiredService<IMediaToolkit>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<FfmpegDecoderPool>()));
        services.TryAddSingleton<OverlayResultCache>();
        services.TryAddSingleton<OverlayComposer>(OverlayComposer.Default());

        // 按流指定推理模型（startOverlay&model=xxx.onnx）：叠加启动写入，分析采样读取
        services.TryAddSingleton<AnalysisModelSelection>();

        // 显式带 logger：起停/首帧/结束日志是排查"frames=0"的主要依据
        services.TryAddSingleton(provider => new YoloOverlayManager(
            provider.GetRequiredService<MediaSourceRegistry>(),
            provider.GetRequiredService<AppOptions>(),
            provider.GetRequiredService<OverlayResultCache>(),
            provider.GetRequiredService<OverlayComposer>(),
            provider.GetRequiredService<IMediaToolkit>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<YoloOverlayManager>(),
            provider.GetService<AnalysisModelSelection>()));
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
        var env = provider.GetRequiredService<AppOptions>().Env;
        foreach (var factory in analyzerFactories)
        {
            var registration = factory(provider);

            // 采样间隔优先取各自 .env 配置（YOLO / OCR 独立），未配置的分析器走全局间隔
            registration = registration.Analyzer switch
            {
                OcrAnalyzer => registration with { SampleIntervalMs = env.OcrSampleIntervalMs },
                YoloAnalyzer => registration with { SampleIntervalMs = env.YoloSampleIntervalMs },
                _ => registration,
            };

            registry.Register(registration);
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
