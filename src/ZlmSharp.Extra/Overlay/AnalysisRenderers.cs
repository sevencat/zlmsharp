using GlyphRaster;
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

/// <summary>
/// 文本（OCR 识别结果）：用内置字体（GlyphRaster）绘制，中文/英文/数字混排都能显示，
/// 不依赖系统字体或图形库。有文本框时画在框下沿，否则画在锚点处，坐标缺失时居中。
/// </summary>
public sealed class TextOverlayRenderer : IOverlayRenderer
{
    public string Id => "text";

    public void Render(RgbImage image, AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(result);

        var fontSize = FontSizeOf(image);
        var padding = Math.Max(2, (int)MathF.Round(fontSize / 8f));
        var font = TextFont.BuiltIn;

        foreach (var primitive in result.Primitives)
        {
            if (primitive.Text is not { Length: > 0 } text)
            {
                continue;
            }

            var layout = font.Layout(text, fontSize);
            var left = primitive.X ?? Math.Max(0f, (image.Width - layout.Width) / 2f);
            var top = primitive.Y is { } anchorY
                ? anchorY + (primitive.Height ?? 0d) + 2d
                : Math.Max(0d, (image.Height - layout.Height) / 2d);

            FrameOverlay.DrawText(
                image.Pixels,
                image.Width,
                image.Height,
                image.Stride,
                (int)MathF.Round((float)left),
                (int)MathF.Round((float)top),
                layout,
                BoxOverlayRenderer.ColorOf(primitive),
                padding);
        }
    }

    /// <summary>按画面高度取字号：1080p 用 24px，720p 用 20px，更小的画面用 16px。</summary>
    private static float FontSizeOf(RgbImage image) =>
        image.Height >= 1080 ? 24f : image.Height >= 720 ? 20f : 16f;
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

    /// <summary>默认组合：框 + 多边形 + 文本（任何分析器的图元都能画出来，中文由内置字体渲染）。</summary>
    public static OverlayComposer Default() =>
        new OverlayComposer()
            .Add(new BoxOverlayRenderer())
            .Add(new PolygonOverlayRenderer())
            .Add(new TextOverlayRenderer());

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
