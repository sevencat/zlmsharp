using System.Numerics;
using System.Text;

namespace GlyphRaster;

public sealed partial class TextFont
{
    /// <summary>
    /// 排版文本：按码点取字形、累加前进宽度、按需换行。
    /// 中文、日文、韩文都是单码点单字形，这里不需要 HarfBuzz 级别的整形。
    /// </summary>
    public TextLayout Layout(string text, float size, TextLayoutOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lineHeight = options.LineHeight > 0f ? options.LineHeight : GetLineHeight(size);
        var ascent = GetAscent(size);
        var maxWidth = options.MaxWidth;

        var glyphs = new List<PositionedGlyph>(text.Length);
        var penX = 0f;
        var lineIndex = 0;
        var widest = 0f;

        foreach (var rune in text.EnumerateRunes())
        {
            var codePoint = rune.Value;
            if (codePoint == '\n')
            {
                widest = MathF.Max(widest, penX);
                penX = 0f;
                lineIndex++;
                continue;
            }

            if (codePoint == '\r')
            {
                continue;
            }

            if (codePoint == '\t')
            {
                var space = GetAdvance(' ', size) * 4f;
                if (maxWidth > 0f && penX > 0f && penX + space > maxWidth)
                {
                    widest = MathF.Max(widest, penX);
                    penX = 0f;
                    lineIndex++;
                }

                penX += space;
                continue;
            }

            var mask = GetGlyphMask(codePoint, size);

            if (maxWidth > 0f && penX > 0f && penX + mask.Advance > maxWidth)
            {
                widest = MathF.Max(widest, penX);
                penX = 0f;
                lineIndex++;
            }

            if (!mask.IsEmpty)
            {
                var baseline = ascent + (lineIndex * lineHeight);
                glyphs.Add(new PositionedGlyph(
                    mask,
                    (int)MathF.Round(penX) + mask.OffsetX,
                    (int)MathF.Round(baseline) + mask.OffsetY));
            }

            penX += mask.Advance;
        }

        widest = MathF.Max(widest, penX);
        var lineCount = lineIndex + 1;
        return new TextLayout(glyphs, widest, lineCount * lineHeight, ascent, lineHeight, lineCount);
    }

    /// <summary>测量单行（或多行取最宽行）的像素宽度。</summary>
    public float Measure(string text, float size)
    {
        ArgumentNullException.ThrowIfNull(text);

        var width = 0f;
        var widest = 0f;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                widest = MathF.Max(widest, width);
                width = 0f;
                continue;
            }

            if (rune.Value == '\r')
            {
                continue;
            }

            if (rune.Value == '\t')
            {
                width += GetAdvance(' ', size) * 4f;
                continue;
            }

            width += GetGlyphMask(rune.Value, size).Advance;
        }

        return MathF.Max(widest, width);
    }

    /// <summary>
    /// 取整段文字的矢量路径集合：按与 <see cref="Layout"/> 相同的规则（<c>\n</c>、<c>\t</c>、
    /// <see cref="TextLayoutOptions.MaxWidth"/> 折行）逐字平移后合并成一组路径。
    /// 坐标原点在首行基线左端，y 轴向下。
    /// </summary>
    public PathCollection GetTextPaths(string text, float size, TextLayoutOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lineHeight = options.LineHeight > 0f ? options.LineHeight : GetLineHeight(size);
        var ascent = GetAscent(size);
        var maxWidth = options.MaxWidth;

        var paths = new List<VectorPath>(text.Length * 2);
        var penX = 0f;
        var lineIndex = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var codePoint = rune.Value;
            if (codePoint == '\n')
            {
                penX = 0f;
                lineIndex++;
                continue;
            }

            if (codePoint == '\r')
            {
                continue;
            }

            if (codePoint == '\t')
            {
                penX += GetAdvance(' ', size) * 4f;
                continue;
            }

            var advance = GetAdvance(codePoint, size);
            if (maxWidth > 0f && penX > 0f && penX + advance > maxWidth)
            {
                penX = 0f;
                lineIndex++;
            }

            var glyphPaths = GetGlyphPaths(codePoint, size);
            if (glyphPaths.Count > 0)
            {
                var offset = Matrix3x2.CreateTranslation(penX, ascent + (lineIndex * lineHeight));
                foreach (var path in glyphPaths)
                {
                    paths.Add(path.Transform(offset));
                }
            }

            penX += advance;
        }

        return paths.Count == 0 ? PathCollection.Empty : new PathCollection(paths);
    }
}
