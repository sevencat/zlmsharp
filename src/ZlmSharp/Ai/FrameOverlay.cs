namespace ZlmSharp.Ai;

/// <summary>
/// 在 RGB24 帧上画检测框和标签（纯托管、无字体/图形库依赖）。
/// 字体是内置的 5×7 位图，只覆盖 0-9 / A-Z / 少量符号，小写会转成大写。
/// </summary>
public static class FrameOverlay
{
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;
    private const int GlyphSpacing = 1;

    private static readonly (byte R, byte G, byte B)[] Palette =
    [
        (255, 64, 64), (64, 255, 64), (64, 128, 255), (255, 200, 0),
        (255, 64, 255), (0, 220, 220), (255, 128, 0), (180, 120, 255),
    ];

    private static readonly Dictionary<char, byte[]> Glyphs = new()
    {
        ['0'] = [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
        ['1'] = [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
        ['2'] = [0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F],
        ['3'] = [0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E],
        ['4'] = [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
        ['5'] = [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
        ['6'] = [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
        ['7'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
        ['8'] = [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
        ['9'] = [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
        ['A'] = [0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
        ['B'] = [0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E],
        ['C'] = [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
        ['D'] = [0x1C, 0x12, 0x11, 0x11, 0x11, 0x12, 0x1C],
        ['E'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
        ['F'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
        ['G'] = [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F],
        ['H'] = [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
        ['I'] = [0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
        ['J'] = [0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0C],
        ['K'] = [0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11],
        ['L'] = [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
        ['M'] = [0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11],
        ['N'] = [0x11, 0x11, 0x19, 0x15, 0x13, 0x11, 0x11],
        ['O'] = [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
        ['P'] = [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
        ['Q'] = [0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D],
        ['R'] = [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
        ['S'] = [0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E],
        ['T'] = [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
        ['U'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
        ['V'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04],
        ['W'] = [0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0A],
        ['X'] = [0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11],
        ['Y'] = [0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04],
        ['Z'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F],
        ['.'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C],
        [','] = [0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C, 0x08],
        ['-'] = [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
        ['_'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F],
        [':'] = [0x00, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00],
        ['/'] = [0x01, 0x02, 0x02, 0x04, 0x08, 0x08, 0x10],
        ['%'] = [0x19, 0x1A, 0x02, 0x04, 0x08, 0x0B, 0x13],
        ['#'] = [0x0A, 0x0A, 0x1F, 0x0A, 0x1F, 0x0A, 0x0A],
        [' '] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
    };

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

    /// <summary>在 (x, y) 处画一行文字；<paramref name="y"/> 是文字左上角（会放在框上方，贴边时落到框内）。</summary>
    public static void DrawLabel(
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
        if (text.Length == 0)
        {
            return;
        }

        var padding = Math.Max(1, scale);
        var textWidth = text.Length * ((GlyphWidth + GlyphSpacing) * scale) - (GlyphSpacing * scale);
        var boxWidth = Math.Min(width, textWidth + (padding * 2));
        var boxHeight = (GlyphHeight * scale) + (padding * 2);

        var left = Math.Max(0, Math.Min(width - boxWidth, (int)MathF.Round(x)));
        var top = (int)MathF.Round(y) - boxHeight;
        if (top < 0)
        {
            top = Math.Max(0, Math.Min(height - boxHeight, (int)MathF.Round(y)));
        }

        FillRect(rgb, width, height, stride, left, top, boxWidth, boxHeight, color);

        var textTop = top + padding;
        var textLeft = left + padding;
        foreach (var character in text)
        {
            var key = char.ToUpperInvariant(character);
            if (!Glyphs.TryGetValue(key, out var glyph))
            {
                textLeft += (GlyphWidth + GlyphSpacing) * scale;
                continue;
            }

            for (var row = 0; row < GlyphHeight; row++)
            {
                var bits = glyph[row];
                for (var column = 0; column < GlyphWidth; column++)
                {
                    if ((bits & (1 << (GlyphWidth - 1 - column))) == 0)
                    {
                        continue;
                    }

                    FillRect(
                        rgb,
                        width,
                        height,
                        stride,
                        textLeft + (column * scale),
                        textTop + (row * scale),
                        scale,
                        scale,
                        (255, 255, 255));
                }
            }

            textLeft += (GlyphWidth + GlyphSpacing) * scale;
        }
    }
}
