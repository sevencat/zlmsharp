using System.Diagnostics;
using ZlmSharp.Ai;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Core;

namespace ZlmSharp.Extra.Ocr;

/// <summary>
/// OCR 作为通用分析层的一个实现：输出 <see cref="AnalysisKind.Text"/> 图元
/// （<c>Text</c> 放识别文字，<c>Points</c> 放文本框四角），叠加管线据此画框并写入中文。
/// </summary>
public sealed class OcrAnalyzer : IStreamAnalyzer, IAnalyzerInfo, IImageAnalysisCapable
{
    public const string AnalyzerId = "ocr";

    private readonly OcrEngineService _engine;

    public OcrAnalyzer(OcrEngineService engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public string Id => AnalyzerId;

    public string DisplayName => "OCR 文字识别";

    public bool Enabled => _engine.Enabled;

    public async Task<AnalysisResult> AnalyzeAsync(FrameSample frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var ocr = await _engine.RunAsync(frame.Image, cancellationToken).ConfigureAwait(false);
            return ToResult(frame, ocr, started);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AnalysisResult(
                AnalyzerId,
                frame.Key,
                DateTimeOffset.Now,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                frame.Image.Width,
                frame.Image.Height,
                [],
                exception.Message);
        }
    }

    public void ValidateModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        if (!string.Equals(model.Trim(), OcrOptions.DefaultModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"不支持的 OCR 模型：{model}（当前仅内置 {OcrOptions.DefaultModel}）");
        }
    }

    /// <remarks>单图分析不做错误兜底：加载/推理失败直接抛出，由端点转成错误响应。</remarks>
    public async Task<AnalysisResult> AnalyzeImageAsync(
        RgbImage image,
        string? model,
        float? confidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateModel(model);

        var started = Stopwatch.GetTimestamp();
        var ocr = await _engine.RunAsync(image, cancellationToken).ConfigureAwait(false);
        var key = StreamKey.Create(null, "image", "single");
        return ToResult(new FrameSample(key, image), ocr, started);
    }

    public AnalyzerDescriptor Describe()
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model"] = _engine.Options.Model,
            ["min_score"] = _engine.Options.MinScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["use_cls"] = _engine.Options.UseDirectionClassification ? "1" : "0",
            ["line_workers"] = _engine.Options.LineWorkerCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["backend"] = "simdpaddleocr",
        };

        return new AnalyzerDescriptor(
            AnalyzerId,
            DisplayName,
            Enabled,
            _engine.CanRun,
            _engine.Options.UseCustomModels ? _engine.Options.DetModelPath : _engine.Options.Model,
            null,
            "cpu",
            "simdpaddleocr",
            [],
            [OcrOptions.DefaultModel],
            _engine.Ready ? [_engine.Options.Model] : [],
            settings,
            _engine.LastError);
    }

    private AnalysisResult ToResult(FrameSample frame, Sdcb.SimdPaddleOCR.PaddleOcrResult ocr, long started)
    {
        var minScore = _engine.Options.MinScore;
        var primitives = new List<AnalysisPrimitive>(ocr.Lines.Length);

        foreach (var line in ocr.Lines)
        {
            if (string.IsNullOrEmpty(line.Text))
            {
                continue;
            }

            // 该实现的 RecognitionScore 是百分制（0~100），统一归一化成 0~1 再与阈值比较
            var score = line.RecognitionScore > 1f ? line.RecognitionScore / 100f : line.RecognitionScore;
            if (score < minScore)
            {
                continue;
            }

            var box = line.Box;
            var left = Math.Min(Math.Min(box.X1, box.X2), Math.Min(box.X3, box.X4));
            var top = Math.Min(Math.Min(box.Y1, box.Y2), Math.Min(box.Y3, box.Y4));
            var right = Math.Max(Math.Max(box.X1, box.X2), Math.Max(box.X3, box.X4));
            var bottom = Math.Max(Math.Max(box.Y1, box.Y2), Math.Max(box.Y3, box.Y4));

            primitives.Add(new AnalysisPrimitive(
                AnalysisKind.Text,
                string.Empty,
                score,
                null,
                left,
                top,
                Math.Max(0, right - left),
                Math.Max(0, bottom - top),
                [
                    new AnalysisPoint(box.X1, box.Y1),
                    new AnalysisPoint(box.X2, box.Y2),
                    new AnalysisPoint(box.X3, box.Y3),
                    new AnalysisPoint(box.X4, box.Y4),
                ],
                line.Text,
                new Dictionary<string, string> { ["source"] = "ocr" }));
        }

        return new AnalysisResult(
            AnalyzerId,
            frame.Key,
            DateTimeOffset.Now,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            frame.Image.Width,
            frame.Image.Height,
            primitives);
    }
}
