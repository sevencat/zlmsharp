using ZlmSharp.Ai;
using ZlmSharp.Ai.Analysis;

namespace ZlmSharp.Extra.Overlay;

/// <summary>检测框 + 标签（对应 YOLO 的默认画法）。</summary>
public sealed class BoxOverlayRenderer : IOverlayRenderer
{
    public string Id => "box";

    public void Render(RgbImage image, AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(result);

        foreach (var primitive in result.Primitives)
        {
            if (primitive.Kind != AnalysisKind.Box ||
                primitive.X is not { } x || primitive.Y is not { } y ||
                primitive.Width is not { } width || primitive.Height is not { } height)
            {
                continue;
            }

            var color = ColorOf(primitive);
            FrameOverlay.DrawRect(image.Pixels, image.Width, image.Height, image.Stride,
                (float)x, (float)y, (float)width, (float)height, color);

            var label = primitive.Score > 0
                ? $"{primitive.Label} {primitive.Score:P0}"
                : primitive.Label;
            FrameOverlay.DrawLabel(image.Pixels, image.Width, image.Height, image.Stride,
                (float)x, (float)y - 9, label, color);
        }
    }

    internal static (byte R, byte G, byte B) ColorOf(AnalysisPrimitive primitive) =>
        primitive.Attributes is { } attributes && attributes.TryGetValue("classId", out var text) &&
        int.TryParse(text, out var classId)
            ? FrameOverlay.ColorOf(classId)
            : FrameOverlay.ColorOf(primitive.Label.GetHashCode(StringComparison.Ordinal));
}

/// <summary>多边形（区域布防 / 分割轮廓 / OCR 文本框）。</summary>
public sealed class PolygonOverlayRenderer : IOverlayRenderer
{
    public string Id => "polygon";

    public void Render(RgbImage image, AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(result);

        foreach (var primitive in result.Primitives)
        {
            if (primitive.Points is not { Count: > 1 } points)
            {
                continue;
            }

            var color = BoxOverlayRenderer.ColorOf(primitive);
            for (var index = 0; index < points.Count; index++)
            {
                var from = points[index];
                var to = points[(index + 1) % points.Count];
                FrameOverlay.DrawLine(image.Pixels, image.Width, image.Height, image.Stride,
                    (float)from.X, (float)from.Y, (float)to.X, (float)to.Y, color);
            }
        }
    }
}

/// <summary>文本（OCR 识别结果）：画在文本锚点处，坐标缺失时居中。</summary>
public sealed class TextOverlayRenderer : IOverlayRenderer
{
    public string Id => "text";

    public void Render(RgbImage image, AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(result);

        foreach (var primitive in result.Primitives)
        {
            if (primitive.Text is not { Length: > 0 } text)
            {
                continue;
            }

            var x = primitive.X ?? Math.Max(0, (image.Width - (text.Length * 12d)) / 2);
            var y = primitive.Y ?? Math.Max(0, image.Height / 2d);
            FrameOverlay.DrawLabel(image.Pixels, image.Width, image.Height, image.Stride,
                (float)x, (float)y, text, BoxOverlayRenderer.ColorOf(primitive));
        }
    }
}

/// <summary>
/// 中文文本（OCR 结果）：用内置 16×16 点阵字库绘制，中文/英文/数字混排都能显示，
/// 不依赖系统字体或图形库。文本画在文本框下沿，越界时回到框内顶部。
/// </summary>
public sealed class ChineseTextOverlayRenderer : IOverlayRenderer
{
    public string Id => "text";

    public void Render(RgbImage image, AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(result);

        // 1080p 及以上用 2 倍字形（32px 高），小画面用 16px，兼顾清晰度与不遮挡
        var scale = image.Height >= 1080 ? 2 : 1;

        foreach (var primitive in result.Primitives)
        {
            if (primitive.Text is not { Length: > 0 } text)
            {
                continue;
            }

            var textWidth = ChineseBitmapFont.MeasureWidth(text, scale);
            var left = (int)Math.Round(primitive.X ?? Math.Max(0, (image.Width - textWidth) / 2d));
            var top = (int)Math.Round((primitive.Y ?? (image.Height / 2d)) + (primitive.Height ?? 0) + 2);
            if (top + (16 * scale) + 4 > image.Height)
            {
                top = (int)Math.Round(primitive.Y ?? Math.Max(0, (image.Height / 2d) - (16 * scale)));
            }

            ChineseBitmapFont.DrawText(
                image.Pixels,
                image.Width,
                image.Height,
                image.Stride,
                left,
                Math.Max(0, top),
                text,
                (255, 255, 255),
                scale);
        }
    }
}

/// <summary>一组 renderer 的合成器：同一路流可以同时画框、多边形与文本。</summary>
public sealed class OverlayComposer
{
    private readonly List<IOverlayRenderer> _renderers = [];

    public IReadOnlyList<IOverlayRenderer> Renderers => _renderers;

    public OverlayComposer Add(IOverlayRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderers.Add(renderer);
        return this;
    }

    /// <summary>默认组合：框 + 多边形 + 中文文本（任何分析器的图元都能画出来）。</summary>
    public static OverlayComposer Default() =>
        new OverlayComposer()
            .Add(new BoxOverlayRenderer())
            .Add(new PolygonOverlayRenderer())
            .Add(new ChineseTextOverlayRenderer());

    public void Render(RgbImage image, AnalysisResult result)
    {
        foreach (var renderer in _renderers)
        {
            renderer.Render(image, result);
        }
    }

    public void Render(RgbImage image, IEnumerable<AnalysisResult> results)
    {
        foreach (var result in results)
        {
            Render(image, result);
        }
    }
}
