namespace GlyphRaster;

/// <summary>8 位 RGB 颜色。</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor White => new(255, 255, 255);

    public static RgbColor Black => new(0, 0, 0);
}

/// <summary>
/// 把 <see cref="TextLayout"/> 的 A8 覆盖位图混到 RGB24 / BGRA32 像素缓冲上。
/// 只做逐像素 alpha 混合，不分配新缓冲、不依赖任何图形库。
/// </summary>
public static class TextPainter
{
    /// <summary>绘制到 RGB24 缓冲（每像素 3 字节，BGR 顺序为 <see cref="RgbColor"/> 的补集时请自行调换）。</summary>
    public static void DrawRgb24(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        TextLayout layout,
        int x,
        int y,
        RgbColor color)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Validate(pixels, width, height, stride, 3);

        foreach (var glyph in layout.Glyphs)
        {
            var mask = glyph.Mask;
            if (mask.IsEmpty)
            {
                continue;
            }

            var left = x + glyph.X;
            var top = y + glyph.Y;
            var minRow = Math.Max(0, -top);
            var maxRow = Math.Min(mask.Height, height - top);
            var minCol = Math.Max(0, -left);
            var maxCol = Math.Min(mask.Width, width - left);

            for (var row = minRow; row < maxRow; row++)
            {
                var coverageBase = row * mask.Width;
                var destination = ((top + row) * stride) + (left * 3);
                for (var col = minCol; col < maxCol; col++)
                {
                    var alpha = mask.Coverage[coverageBase + col];
                    if (alpha == 0)
                    {
                        continue;
                    }

                    var p = destination + (col * 3);
                    var inv = 255 - alpha;
                    pixels[p] = (byte)(((pixels[p] * inv) + (color.R * alpha)) / 255);
                    pixels[p + 1] = (byte)(((pixels[p + 1] * inv) + (color.G * alpha)) / 255);
                    pixels[p + 2] = (byte)(((pixels[p + 2] * inv) + (color.B * alpha)) / 255);
                }
            }
        }
    }

    /// <summary>绘制到 BGRA32 缓冲（A 通道保持不变，仅做 RGB 混合）。</summary>
    public static void DrawBgra32(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        TextLayout layout,
        int x,
        int y,
        RgbColor color)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Validate(pixels, width, height, stride, 4);

        foreach (var glyph in layout.Glyphs)
        {
            var mask = glyph.Mask;
            if (mask.IsEmpty)
            {
                continue;
            }

            var left = x + glyph.X;
            var top = y + glyph.Y;
            var minRow = Math.Max(0, -top);
            var maxRow = Math.Min(mask.Height, height - top);
            var minCol = Math.Max(0, -left);
            var maxCol = Math.Min(mask.Width, width - left);

            for (var row = minRow; row < maxRow; row++)
            {
                var coverageBase = row * mask.Width;
                var destination = ((top + row) * stride) + (left * 4);
                for (var col = minCol; col < maxCol; col++)
                {
                    var alpha = mask.Coverage[coverageBase + col];
                    if (alpha == 0)
                    {
                        continue;
                    }

                    var p = destination + (col * 4);
                    var inv = 255 - alpha;
                    pixels[p] = (byte)(((pixels[p] * inv) + (color.B * alpha)) / 255);
                    pixels[p + 1] = (byte)(((pixels[p + 1] * inv) + (color.G * alpha)) / 255);
                    pixels[p + 2] = (byte)(((pixels[p + 2] * inv) + (color.R * alpha)) / 255);
                }
            }
        }
    }

    private static void Validate(Span<byte> pixels, int width, int height, int stride, int bytesPerPixel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * bytesPerPixel);
        ArgumentOutOfRangeException.ThrowIfLessThan((long)pixels.Length, (long)stride * height);
    }
}
