using GlyphRaster;
using ZlmSharp.Extra.Yolo;

namespace ZlmSharp.Extra.Overlay;

/// <summary>
/// 在 RGB24 帧上画检测框和标签（纯托管、无图形库依赖）。
/// 文字统一交给 <see cref="GlyphRaster"/>（内置文泉驿微米黑）光栅化，
/// 中文、拉丁字母、数字都能显示，字形按 (码点, 字号) 缓存。
/// </summary>
public static class FrameOverlay
{
    /// <summary>标签字号（em 高度）相对 <c>scale</c> 的基准值：scale=2 → 16px。</summary>
    private const float LabelFontSize = 8f;

    /// <summary>内置字体在首次绘制文字时才解压（避免只用矩形时也付解压代价）。</summary>
    private static readonly Lazy<TextFont> LabelFont = new(
        static () => TextFont.BuiltIn,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly (byte R, byte G, byte B)[] Palette =
    [
        (255, 64, 64), (64, 255, 64), (64, 128, 255), (255, 200, 0),
        (255, 64, 255), (0, 220, 220), (255, 128, 0), (180, 120, 255),
    ];

    /// <summary>按类别取一个稳定颜色。</summary>
    public static (byte R, byte G, byte B) ColorOf(int classId) =>
        Palette[(classId & 0x7FFFFFFF) % Palette.Length];

    /// <summary>画所有检测框 + 类别名/置信度标签。</summary>
    public static void DrawDetections(
        Span<byte> rgb,
        int width,
        int height,
        int stride,
        IReadOnlyList<YoloObject> objects,
        int scale = 2)
    {
        foreach (var item in objects)
        {
            var color = ColorOf(item.ClassId);
            DrawRect(rgb, width, height, stride, item.X, item.Y, item.Width, item.Height, color, thickness: 2);

            var track = item.TrackId > 0 ? $"#{item.TrackId} " : string.Empty;
            var label = string.IsNullOrEmpty(item.ClassName)
                ? $"{track}#{item.ClassId} {item.Confidence * 100:F0}%"
                : $"{track}{item.ClassName} {item.Confidence * 100:F0}%";
            DrawLabel(rgb, width, height, stride, item.X, item.Y, label, color, scale);
        }
    }

    /// <summary>画矩形边框（坐标按像素，自动裁剪到画面内）。</summary>
    public static void DrawRect(
        Span<byte> rgb,
        int width,
        int height,
        int stride,
        float x,
        float y,
        float boxWidth,
        float boxHeight,
        (byte R, byte G, byte B) color,
        int thickness = 2)
    {
        var left = (int)MathF.Round(x);
        var top = (int)MathF.Round(y);
        var right = left + Math.Max(1, (int)MathF.Round(boxWidth));
        var bottom = top + Math.Max(1, (int)MathF.Round(boxHeight));

        for (var t = 0; t < Math.Max(1, thickness); t++)
        {
            FillRect(rgb, width, height, stride, left + t, top + t, right - left - (t * 2), 1, color);
            FillRect(rgb, width, height, stride, left + t, bottom - t - 1, right - left - (t * 2), 1, color);
            FillRect(rgb, width, height, stride, left + t, top + t, 1, bottom - top - (t * 2), color);
            FillRect(rgb, width, height, stride, right - t - 1, top + t, 1, bottom - top - (t * 2), color);
        }
    }

    /// <summary>画线段（Bresenham，<paramref name="thickness"/> 为像素粗细），自动裁剪。</summary>
    public static void DrawLine(
        Span<byte> rgb,
        int width,
        int height,
        int stride,
        float x1,
        float y1,
        float x2,
        float y2,
        (byte R, byte G, byte B) color,
        int thickness = 1)
    {
        var startX = (int)MathF.Round(x1);
        var startY = (int)MathF.Round(y1);
        var endX = (int)MathF.Round(x2);
        var endY = (int)MathF.Round(y2);

        var deltaX = Math.Abs(endX - startX);
        var stepX = startX < endX ? 1 : -1;
        var deltaY = -Math.Abs(endY - startY);
        var stepY = startY < endY ? 1 : -1;
        var error = deltaX + deltaY;
        var size = Math.Max(1, thickness);
        var half = size / 2;

        while (true)
        {
            FillRect(rgb, width, height, stride, startX - half, startY - half, size, size, color);

            if (startX == endX && startY == endY)
            {
                break;
            }

            var doubled = error * 2;
            if (doubled >= deltaY)
            {
                error += deltaY;
                startX += stepX;
            }

            if (doubled <= deltaX)
            {
                error += deltaX;
                startY += stepY;
            }
        }
    }

    /// <summary>画纯色矩形（自动裁剪）。</summary>
    public static void FillRect(
        Span<byte> rgb,
        int width,
        int height,
        int stride,
        int x,
        int y,
        int rectWidth,
        int rectHeight,
        (byte R, byte G, byte B) color)
    {
        var left = Math.Max(0, x);
        var top = Math.Max(0, y);
        var right = Math.Min(width, x + rectWidth);
        var bottom = Math.Min(height, y + rectHeight);

        for (var row = top; row < bottom; row++)
        {
            var offset = (row * stride) + (left * 3);
            for (var column = left; column < right; column++)
            {
                rgb[offset++] = color.R;
                rgb[offset++] = color.G;
                rgb[offset++] = color.B;
            }
        }
    }

    /// <summary>
    /// 在框左上角 (x, y) 处画一行标签：底色取 <paramref name="color"/>，文字为白色。
    /// 标签默认放在框上方，贴边时落回框内左上角。<paramref name="scale"/> 控制字号（1..6）。
    /// </summary>
    /// <returns>占用的像素高度；文本为空时返回 0。</returns>
    public static int DrawLabel(
        Span<byte> rgb,
        int width,
        int height,
        int stride,
        float x,
        float y,
        string text,
        (byte R, byte G, byte B) color,
        int scale = 2)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        scale = Math.Clamp(scale, 1, 6);
        var layout = LabelFont.Value.Layout(text, LabelFontSize * scale);
        var padding = Math.Max(2, scale);

        var boxWidth = Math.Min(width, (int)MathF.Ceiling(layout.Width) + (padding * 2));
        var boxHeight = (int)MathF.Ceiling(layout.Height) + (padding * 2);

        var left = Math.Clamp((int)MathF.Round(x), 0, Math.Max(0, width - boxWidth));
        var top = (int)MathF.Round(y) - boxHeight;
        if (top < 0)
        {
            top = Math.Clamp((int)MathF.Round(y), 0, Math.Max(0, height - boxHeight));
        }

        return DrawText(rgb, width, height, stride, left, top, layout, color, padding);
    }

    /// <summary>
    /// 在 (x, y)（左上角）画一段带底色的白色文本，返回占用的像素高度。
    /// 先铺底色再逐字形做 alpha 混合，画面外部分自动裁剪。
    /// </summary>
    public static int DrawText(
        Span<byte> rgb,
        int width,
        int height,
        int stride,
        int x,
        int y,
        TextLayout layout,
        (byte R, byte G, byte B) background,
        int padding = 2)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var boxWidth = Math.Min(width, (int)MathF.Ceiling(layout.Width) + (padding * 2));
        var boxHeight = Math.Min(height, (int)MathF.Ceiling(layout.Height) + (padding * 2));
        if (boxWidth <= 0 || boxHeight <= 0)
        {
            return 0;
        }

        var left = Math.Clamp(x, 0, Math.Max(0, width - boxWidth));
        var top = Math.Clamp(y, 0, Math.Max(0, height - boxHeight));

        FillRect(rgb, width, height, stride, left, top, boxWidth, boxHeight, background);
        TextPainter.DrawRgb24(rgb, width, height, stride, layout, left + padding, top + padding, RgbColor.White);
        return boxHeight;
    }

    /// <summary>用内置字体测量文本的像素尺寸（不含底色 padding）。</summary>
    public static (float Width, float Height) MeasureText(string text, float fontSize)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0f)
        {
            return (0f, 0f);
        }

        var layout = LabelFont.Value.Layout(text, fontSize);
        return (layout.Width, layout.Height);
    }
}
