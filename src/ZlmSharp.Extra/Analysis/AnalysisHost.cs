using ZlmSharp.Ai;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Configuration;
using ZlmSharp.Extra.Capture;

namespace ZlmSharp.Extra.Analysis;

/// <summary>
/// 宿主侧的分析入口：分析器清单（<see cref="IAnalyzerCatalog"/>）与单图分析（<see cref="IImageAnalysisService"/>）。
/// 单图分析的取图（PPM 解析 / ffmpeg 抓帧）在扩展包内完成，核心 API 只传原始字节。
/// </summary>
public sealed class AnalysisHost : IAnalyzerCatalog, IImageAnalysisService
{
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromSeconds(10);

    private readonly AnalyzerRegistry _registry;
    private readonly AppOptions _options;

    public AnalysisHost(AnalyzerRegistry registry, AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        _registry = registry;
        _options = options;
    }

    public IReadOnlyList<string> ImageFormats => ["ppm"];

    public IReadOnlyList<AnalyzerDescriptor> List() =>
        _registry.All.Select(Describe).ToArray();

    public AnalyzerDescriptor? Get(string analyzerId)
    {
        var registration = _registry.Get(analyzerId);
        return registration is null ? null : Describe(registration);
    }

    public async Task<AnalysisResult> AnalyzeAsync(ImageAnalysisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var registration = _registry.Get(request.AnalyzerId)
            ?? throw new InvalidOperationException($"分析器未注册：{request.AnalyzerId}");

        if (registration.Analyzer is not IImageAnalysisCapable capable)
        {
            throw new InvalidOperationException($"分析器 {request.AnalyzerId} 不支持单图分析");
        }

        // 先校验模型名，避免白跑一次取帧/解码
        capable.ValidateModel(request.Model);

        RgbImage image;
        if (request.SourceUrl is { Length: > 0 } url)
        {
            image = await FfmpegFrameGrabber.GrabAsync(
                _options.Env.ResolveFfmpegPath(),
                url,
                GrabTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        else if (request.Image is { } bytes)
        {
            if (bytes.Length == 0)
            {
                throw new ArgumentException("图片内容为空");
            }

            if (!PpmImage.TryParse(bytes.Span, out var parsed, out var error))
            {
                throw new InvalidDataException(error);
            }

            image = parsed;
        }
        else
        {
            throw new ArgumentException("请提供图片内容或源地址");
        }

        return await capable.AnalyzeImageAsync(image, request.Model, request.Confidence, cancellationToken)
            .ConfigureAwait(false);
    }

    private static AnalyzerDescriptor Describe(AnalyzerRegistration registration)
    {
        var analyzer = registration.Analyzer;
        return analyzer is IAnalyzerInfo info
            ? info.Describe()
            : new AnalyzerDescriptor(
                analyzer.Id,
                analyzer.DisplayName,
                analyzer.Enabled,
                analyzer.Enabled,
                null,
                null,
                null,
                null,
                [],
                [],
                [],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["outputs"] = registration.Outputs.ToString().ToLowerInvariant(),
                    ["sample_interval_ms"] = registration.SampleIntervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
    }
}
