using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZlmSharp.Ai;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Extra.Yolo;

namespace ZlmSharp.Extra;

/// <summary>注册参数：结果去向与采样间隔。</summary>
public sealed class AnalyzerRegistrationOptions
{
    /// <summary>结果去向；默认只存快照（<c>analyzeResult</c> 可查）。叠加需要 <see cref="AnalysisOutputs.Overlay"/>。</summary>
    public AnalysisOutputs Outputs { get; set; } = AnalysisOutputs.Snapshot;

    /// <summary>同一路流两次分析的最小间隔（毫秒）；0 表示用全局 <c>ZLM_ANALYSIS_SAMPLE_INTERVAL_MS</c>。</summary>
    public int SampleIntervalMs { get; set; }
}

/// <summary>
/// 扩展包注册 DSL：第三方算法（<see cref="IStreamAnalyzer"/> / <see cref="IOverlayRenderer"/> / <see cref="IAnalysisSink"/>
/// / <see cref="IYoloBackendFactory"/>）从这里接入，核心库不需要任何改动。
/// </summary>
public interface IExtraBuilder
{
    IServiceCollection Services { get; }

    /// <summary>注册一个由 DI 创建的分析器单例。</summary>
    IExtraBuilder AddAnalyzer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TAnalyzer>(
        Action<AnalyzerRegistrationOptions>? configure = null)
        where TAnalyzer : class, IStreamAnalyzer;

    /// <summary>注册一个现成的分析器实例。</summary>
    IExtraBuilder AddAnalyzer(IStreamAnalyzer analyzer, Action<AnalyzerRegistrationOptions>? configure = null);

    /// <summary>注册一个由 DI 创建的结果旁路。</summary>
    IExtraBuilder AddSink<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSink>()
        where TSink : class, IAnalysisSink;

    /// <summary>注册一个现成的结果旁路。</summary>
    IExtraBuilder AddSink(IAnalysisSink sink);

    /// <summary>
    /// 接入 YOLO 推理内核（如 SimdYolo 适配器）；不调用时退化为“无后端”，检测入口按未启用处理。
    /// </summary>
    IExtraBuilder AddYoloBackendFactory<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactory>()
        where TFactory : class, IYoloBackendFactory;
}

internal sealed class ExtraBuilder : IExtraBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<Func<IServiceProvider, AnalyzerRegistration>> _analyzerFactories = [];
    private readonly List<Func<IServiceProvider, IAnalysisSink>> _sinkFactories = [];

    public ExtraBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public IServiceCollection Services => _services;

    public IReadOnlyList<Func<IServiceProvider, AnalyzerRegistration>> AnalyzerFactories => _analyzerFactories;

    public IReadOnlyList<Func<IServiceProvider, IAnalysisSink>> SinkFactories => _sinkFactories;

    public IExtraBuilder AddAnalyzer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TAnalyzer>(
        Action<AnalyzerRegistrationOptions>? configure = null)
        where TAnalyzer : class, IStreamAnalyzer
    {
        _services.TryAddSingleton<TAnalyzer>();
        _analyzerFactories.Add(provider => BuildRegistration(provider.GetRequiredService<TAnalyzer>(), configure));
        return this;
    }

    public IExtraBuilder AddAnalyzer(IStreamAnalyzer analyzer, Action<AnalyzerRegistrationOptions>? configure = null)
    {
        _analyzerFactories.Add(_ => BuildRegistration(analyzer, configure));
        return this;
    }

    public IExtraBuilder AddSink<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSink>()
        where TSink : class, IAnalysisSink
    {
        _services.TryAddSingleton<TSink>();
        _sinkFactories.Add(provider => provider.GetRequiredService<TSink>());
        return this;
    }

    public IExtraBuilder AddSink(IAnalysisSink sink)
    {
        _sinkFactories.Add(_ => sink);
        return this;
    }

    public IExtraBuilder AddYoloBackendFactory<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactory>()
        where TFactory : class, IYoloBackendFactory
    {
        _services.Replace(ServiceDescriptor.Singleton<IYoloBackendFactory, TFactory>());
        return this;
    }

    private static AnalyzerRegistration BuildRegistration(
        IStreamAnalyzer analyzer,
        Action<AnalyzerRegistrationOptions>? configure)
    {
        var options = new AnalyzerRegistrationOptions();
        configure?.Invoke(options);
        return new AnalyzerRegistration(analyzer)
        {
            Outputs = options.Outputs,
            SampleIntervalMs = options.SampleIntervalMs,
        };
    }
}
