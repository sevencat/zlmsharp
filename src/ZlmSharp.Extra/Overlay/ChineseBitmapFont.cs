using System.Reflection;

namespace ZlmSharp.Extra.Overlay;

/// <summary>
/// 内置中文点阵字库（16×16，GB2312 全集 + ASCII 半角）：用于把 OCR 识别出的中文写进叠加画面，
/// 不依赖任何字体文件或图形库，裁剪/AOT 安全。
/// </summary>
/// <remarks>
/// 资源格式（LittleEndian）：<c>"HZF1"</c> + 版本(1 字节) + 字符数(int32) +
/// 码点表(int32×N，升序) + 宽度表(byte×N，8 或 16) + 字形表(32 字节×N，16 行 × 2 字节，MSB 在左)。
/// </remarks>
internal static class ChineseBitmapFont
{
    private const string ResourceName = "ZlmSharp.Extra.Overlay.Fonts.hzk16.bin";
    private const int GlyphHeight = 16;
    private const int GlyphBytes = 32;
    private const byte BackgroundR = 0;
    private const byte BackgroundG = 0;
    private const byte BackgroundB = 0;

    private static readonly Lazy<FontData?> Data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>字库是否可用（嵌入资源缺失时为 false，叠加时退回只画框）。</summary>
    public static bool Available => Data.Value is not null;

    /// <summary>按缩放计算文本像素宽度（含字间距）。</summary>
    public static int MeasureWidth(string text, int scale = 1)
    {
        if (string.IsNullOrEmpty(text) || Data.Value is not { } data)
        {
            return 0;
        }

        scale = Math.Clamp(scale, 1, 4);
        var width = 0;
        foreach (var ch in text)
        {
            width += (data.WidthOf(ch) * scale) + scale;
        }

        return width;
    }

    /// <summary>
    /// 在 RGB24 缓冲上绘制一段文本：先铺不透明黑底再写白字，保证在任意画面上可读。
    /// 越界部分自动裁剪。
    /// </summary>
    public static void DrawText(
        byte[] pixels,
        int width,
        int height,
        int stride,
        int x,
        int y,
        string text,
        (byte R, byte G, byte B) color,
        int scale = 1)
    {
        if (string.IsNullOrEmpty(text) || Data.Value is not { } data)
        {
            return;
        }

        scale = Math.Clamp(scale, 1, 4);
        var textWidth = MeasureWidth(text, scale);
        if (textWidth <= 0)
        {
            return;
        }

        var boxHeight = GlyphHeight * scale;

        // 背景：比文本大 2px，避免贴边看不清
        FillRect(pixels, width, height, stride, x - 2, y - 2, textWidth + 4, boxHeight + 4, (BackgroundR, BackgroundG, BackgroundB));

        var cursor = x;
        foreach (var ch in text)
        {
            var glyphWidth = data.WidthOf(ch);
            if (data.TryGetGlyph(ch, out var glyph, out var actualWidth))
            {
                glyphWidth = actualWidth;
                for (var row = 0; row < GlyphHeight; row++)
                {
                    for (var col = 0; col < glyphWidth; col++)
                    {
                        if ((glyph[row * 2 + col / 8] & (0x80 >> (col % 8))) == 0)
                        {
                            continue;
                        }

                        FillRect(pixels, width, height, stride,
                            cursor + col * scale, y + row * scale, scale, scale, color);
                    }
                }
            }

            cursor += glyphWidth * scale + scale;
        }
    }

    private static void FillRect(
        byte[] pixels,
        int width,
        int height,
        int stride,
        int x,
        int y,
        int rectWidth,
        int rectHeight,
        (byte R, byte G, byte B) color)
    {
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(width, x + rectWidth);
        var y1 = Math.Min(height, y + rectHeight);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        for (var row = y0; row < y1; row++)
        {
            var offset = (row * stride) + (x0 * 3);
            for (var col = x0; col < x1; col++)
            {
                pixels[offset] = color.R;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.B;
                offset += 3;
            }
        }
    }

    private static FontData? Load()
    {
        using var stream = typeof(ChineseBitmapFont).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new BinaryReader(stream);
        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != (byte)'H' || magic[1] != (byte)'Z' || magic[2] != (byte)'F' || magic[3] != (byte)'1')
        {
            return null;
        }

        _ = reader.ReadByte(); // 版本
        var count = reader.ReadInt32();
        if (count <= 0 || count > 100_000)
        {
            return null;
        }

        var codePoints = new int[count];
        for (var index = 0; index < count; index++)
        {
            codePoints[index] = reader.ReadInt32();
        }

        var widths = reader.ReadBytes(count);
        var glyphs = reader.ReadBytes(count * GlyphBytes);
        if (widths.Length != count || glyphs.Length != count * GlyphBytes)
        {
            return null;
        }

        return new FontData(codePoints, widths, glyphs);
    }

    private sealed class FontData
    {
        private readonly int[] _codePoints;
        private readonly byte[] _widths;
        private readonly byte[] _glyphs;

        public FontData(int[] codePoints, byte[] widths, byte[] glyphs)
        {
            _codePoints = codePoints;
            _widths = widths;
            _glyphs = glyphs;
        }

        public int WidthOf(char ch)
        {
            var index = IndexOf(ch);
            return index < 0 ? (ch < 0x80 ? 8 : 16) : _widths[index];
        }

        public bool TryGetGlyph(char ch, out ReadOnlySpan<byte> glyph, out int width)
        {
            var index = IndexOf(ch);
            if (index < 0)
            {
                glyph = default;
                width = 0;
                return false;
            }

            glyph = _glyphs.AsSpan(index * GlyphBytes, GlyphBytes);
            width = _widths[index];
            return true;
        }

        /// <summary>码点表按升序存放，二分查找。</summary>
        private int IndexOf(char ch)
        {
            var target = (int)ch;
            var low = 0;
            var high = _codePoints.Length - 1;
            while (low <= high)
            {
                var mid = low + ((high - low) >> 1);
                var value = _codePoints[mid];
                if (value == target)
                {
                    return mid;
                }

                if (value < target)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return -1;
        }
    }
}
