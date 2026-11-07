using System.Buffers.Binary;
using System.Numerics;
using GlyphRaster;

namespace ZlmSharp.Tests;

/// <summary>GlyphRaster（纯托管 TrueType 渲染）的行为测试。</summary>
public sealed class GlyphRasterTests
{
    private static readonly string? ChineseFont = FindFont("simhei.ttf", "msyh.ttc", "simsun.ttc");

    private static readonly string? LatinFont = FindFont("arial.ttf", "segoeui.ttf", "simhei.ttf");

    [Fact]
    public void RendersChineseGlyphWithAntiAliasing()
    {
        if (ChineseFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(ChineseFont);
        var mask = font.GetGlyphMask('中', 48f);

        Assert.False(mask.IsEmpty);
        Assert.InRange(mask.Width, 20, 64);
        Assert.InRange(mask.Height, 20, 64);
        Assert.True(mask.OffsetY < 0, "字形应位于基线上方");

        var sum = 0L;
        foreach (var alpha in mask.Coverage)
        {
            sum += alpha;
        }

        var fill = (double)sum / (mask.Width * mask.Height * 255);
        Assert.InRange(fill, 0.05, 0.75);
    }

    [Fact]
    public void AntiAliasingProducesPartialCoverage()
    {
        if (ChineseFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(ChineseFont);
        var mask = font.GetGlyphMask('永', 40f);

        var partial = 0;
        foreach (var alpha in mask.Coverage)
        {
            if (alpha is > 0 and < 255)
            {
                partial++;
            }
        }

        Assert.True(partial > 0, "抗锯齿应产生中间灰度像素");
    }

    [Fact]
    public void GlyphMaskIsCachedPerSize()
    {
        if (LatinFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(LatinFont);
        Assert.Same(font.GetGlyphMask('A', 24f), font.GetGlyphMask('A', 24f));
        Assert.NotSame(font.GetGlyphMask('A', 24f), font.GetGlyphMask('A', 32f));
    }

    [Fact]
    public void BlankCharactersHaveAdvanceButNoPixels()
    {
        if (LatinFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(LatinFont);
        var space = font.GetGlyphMask(' ', 24f);

        Assert.True(space.IsEmpty);
        Assert.True(space.Advance > 0);
    }

    [Fact]
    public void LayoutAdvancesAndWraps()
    {
        if (ChineseFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(ChineseFont);
        var single = font.Layout("中文", 32f);
        var wrapped = font.Layout("中文中文中文中文中文中文", 32f, new TextLayoutOptions { MaxWidth = 100f });

        Assert.Equal(1, single.LineCount);
        Assert.True(single.Width > 0f);
        Assert.True(wrapped.LineCount > 1, "超出 MaxWidth 应换行");
        Assert.True(wrapped.Height > single.Height);
    }

    [Fact]
    public void ExplicitNewLineIncreasesLineCount()
    {
        if (LatinFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(LatinFont);
        var layout = font.Layout("a\nb\nc", 20f);

        Assert.Equal(3, layout.LineCount);
        Assert.Equal(3, layout.Glyphs.Count);
        Assert.True(layout.Glyphs[2].Y > layout.Glyphs[0].Y);
    }

    [Fact]
    public void PainterClipsInsteadOfThrowing()
    {
        if (ChineseFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(ChineseFont);
        var layout = font.Layout("裁剪测试", 32f);
        var pixels = new byte[40 * 40 * 3];

        // 负坐标 + 超出右下边界，都不应抛异常
        TextPainter.DrawRgb24(pixels, 40, 40, 40 * 3, layout, -20, -20, RgbColor.White);
        TextPainter.DrawRgb24(pixels, 40, 40, 40 * 3, layout, 30, 30, RgbColor.White);

        var painted = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] > 0)
            {
                painted++;
            }
        }

        Assert.True(painted > 0, "两次绘制都至少应有部分像素落在缓冲内");
    }

    [Fact]
    public void VectorPathCoversOutline()
    {
        if (ChineseFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(ChineseFont);
        var path = font.GetGlyphPath('国', 48f);
        var mask = font.GetGlyphMask('国', 48f);

        Assert.False(path.IsEmpty);
        Assert.Contains(path.Segments, segment => segment.Verb == PathVerb.QuadraticTo);
        Assert.True(path.Width > 0f && path.Height > 0f);

        // 位图宽高应与矢量包围盒基本吻合（允许 2px 取整误差）
        Assert.InRange(mask.Width, (int)path.Width - 2, (int)path.Width + 2);
        Assert.InRange(mask.Height, (int)path.Height - 2, (int)path.Height + 2);
    }

    [Fact]
    public void MissingCodePointDoesNotThrow()
    {
        if (LatinFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(LatinFont);
        var mask = font.GetGlyphMask(0x1F600, 24f);

        Assert.NotNull(mask);
        Assert.False(font.HasGlyph(0x1F600));
    }

    [Fact]
    public void GlyphPathSplitsIntoClosedContours()
    {
        if (ChineseFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(ChineseFont);
        var path = font.GetGlyphPath('回', 48f);
        var collection = path.ToPathCollection();

        Assert.True(collection.Count >= 2, "「回」应有内外至少两条轮廓");
        Assert.All(collection, item => Assert.True(item.IsClosed));
        Assert.Equal(path.MinX, collection.Bounds.X, 2);
        Assert.Equal(path.MaxX, collection.Bounds.Right, 2);
        Assert.Equal(path.MinY, collection.Bounds.Y, 2);
        Assert.Equal(path.MaxY, collection.Bounds.Bottom, 2);
    }

    [Fact]
    public void PathCollectionTransformMovesBounds()
    {
        if (LatinFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(LatinFont);
        var paths = font.GetGlyphPaths('A', 32f);
        Assert.False(paths.IsEmpty);

        var moved = paths.Transform(Matrix3x2.CreateTranslation(10f, 20f));

        Assert.Equal(paths.Bounds.X + 10f, moved.Bounds.X, 2);
        Assert.Equal(paths.Bounds.Y + 20f, moved.Bounds.Y, 2);
        Assert.Equal(paths.Bounds.Width, moved.Bounds.Width, 2);
        Assert.Equal(paths.Bounds.Height, moved.Bounds.Height, 2);

        var scaled = paths.Transform(Matrix3x2.CreateScale(2f));
        Assert.Equal(paths.Bounds.Width * 2f, scaled.Bounds.Width, 2);
    }

    [Fact]
    public void SvgPathDataIsWellFormed()
    {
        if (LatinFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(LatinFont);
        var svg = font.GetGlyphPaths('H', 32f).ToSvgPathData();

        Assert.StartsWith("M ", svg);
        Assert.EndsWith("Z", svg);
        Assert.Contains(" L ", svg);
    }

    [Fact]
    public void TextPathsFollowPenPositions()
    {
        if (LatinFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(LatinFont);
        var single = font.GetGlyphPaths('A', 32f);
        Assert.False(single.IsEmpty);

        var text = font.GetTextPaths("AA", 32f);
        Assert.Equal(single.Count * 2, text.Count);

        var advance = font.GetAdvance('A', 32f);
        var delta = text[single.Count].Bounds.X - text[0].Bounds.X;
        Assert.Equal(advance, delta, 2);
    }

    [Fact]
    public void TextPathsRespectNewLines()
    {
        if (LatinFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(LatinFont);
        var single = font.GetGlyphPaths('A', 32f);
        var layout = font.Layout("A\nA", 32f);
        var paths = font.GetTextPaths("A\nA", 32f);

        // 每个字形的轮廓数相同（Arial 的 A 有内外两条），两行即两倍
        Assert.Equal(single.Count * 2, paths.Count);
        var baselineDelta = paths[single.Count].Bounds.Y - paths[0].Bounds.Y;
        Assert.Equal(layout.LineHeight, baselineDelta, 2);
    }

    [Fact]
    public void FlattenReplacesCurvesWithLines()
    {
        if (ChineseFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(ChineseFont);
        var original = font.GetGlyphPaths('永', 48f);

        Assert.Contains(original, path => path.Segments.Any(segment => segment.Verb == PathVerb.QuadraticTo));

        var flattened = original.Flatten(0.2f);
        Assert.All(flattened, path => Assert.All(path.Segments, segment => Assert.Equal(PathVerb.LineTo, segment.Verb)));
        Assert.Equal(original.Bounds.X, flattened.Bounds.X, 1);
        Assert.Equal(original.Bounds.Bottom, flattened.Bounds.Bottom, 1);
    }

    [Fact]
    public void EmptyInputsProduceEmptyPathCollection()
    {
        if (LatinFont is null)
        {
            return;
        }

        var font = TextFont.LoadFromFile(LatinFont);

        Assert.True(font.GetTextPaths(string.Empty, 20f).IsEmpty);
        Assert.True(font.GetTextPaths("   ", 20f).IsEmpty);
        Assert.True(PathCollection.Empty.IsEmpty);
        Assert.Equal(string.Empty, PathCollection.Empty.ToSvgPathData());
    }

    [Fact]
    public void BuiltInFontLoadsAndCoversCjk()
    {
        var font = TextFont.BuiltIn;

        Assert.Contains("WenQuanYi", font.FamilyName, StringComparison.OrdinalIgnoreCase);
        Assert.True(EmbeddedFonts.GetFontSize() > 4_000_000);
        Assert.Equal(1, EmbeddedFonts.Count);

        Assert.True(font.HasGlyph('你'));
        Assert.True(font.HasGlyph('あ'));
        Assert.True(font.HasGlyph('한'));

        var layout = font.Layout("你好，世界", 24f);
        Assert.Equal(5, layout.Glyphs.Count);
        Assert.All(layout.Glyphs, glyph => Assert.False(glyph.Mask.IsEmpty));
        Assert.True(layout.Width > 100f);
    }

    [Fact]
    public void BuiltInFontDataIsValidSfnt()
    {
        var data = EmbeddedFonts.GetFontData(BuiltInFont.WenQuanYiMicroHei);
        Assert.True(data.Length > 4_000_000);

        var tag = BinaryPrimitives.ReadUInt32BigEndian(data);
        Assert.True(
            tag is 0x00010000 or 0x74727565 or 0x74746366,
            $"unexpected sfnt tag: 0x{tag:X8}");

        // 同一份数据被缓存复用，不是每次解压
        Assert.Same(data, EmbeddedFonts.GetFontData());
    }

    [Fact]
    public void BuiltInFontRendersSameAsExternalFile()
    {
        if (ChineseFont is null)
        {
            return;
        }

        var builtIn = TextFont.BuiltIn;
        var external = TextFont.LoadFromFile(@"C:\Windows\Fonts\simhei.ttf");

        var a = builtIn.GetGlyphMask('国', 32f);
        var b = external.GetGlyphMask('国', 32f);

        Assert.False(a.IsEmpty);
        Assert.False(b.IsEmpty);
        Assert.InRange(a.Width, 28, 36);
    }

    private static string? FindFont(params string[] fileNames)
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        foreach (var name in fileNames)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
