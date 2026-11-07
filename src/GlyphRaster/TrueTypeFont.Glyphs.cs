namespace GlyphRaster;

public sealed partial class TrueTypeFont
{
    // 复合字形 component flags
    private const int Arg1And2AreWords = 0x0001;
    private const int ArgsAreXyValues = 0x0002;
    private const int HaveScale = 0x0008;
    private const int MoreComponents = 0x0020;
    private const int HaveXYScale = 0x0040;
    private const int HaveTwoByTwo = 0x0080;

    /// <summary>仿射变换（字体单位）。</summary>
    private readonly record struct Transform(float A, float B, float C, float D, float Dx, float Dy)
    {
        public static Transform Identity => new(1f, 0f, 0f, 1f, 0f, 0f);

        public OutlinePoint Apply(OutlinePoint p)
            => new((p.X * A) + (p.Y * C) + Dx, (p.X * B) + (p.Y * D) + Dy, p.OnCurve);

        /// <summary>先应用 <paramref name="child"/>，再应用自身。</summary>
        public Transform Then(in Transform child) => new(
            (A * child.A) + (C * child.B),
            (B * child.A) + (D * child.B),
            (A * child.C) + (C * child.D),
            (B * child.C) + (D * child.D),
            (A * child.Dx) + (C * child.Dy) + Dx,
            (B * child.Dx) + (D * child.Dy) + Dy);
    }

    /// <summary>取字形轮廓（字体单位，y 轴向上）。空字形返回 false。</summary>
    public bool TryGetOutline(int glyphIndex, out GlyphOutline outline)
    {
        outline = GlyphOutline.Empty;
        if (glyphIndex < 0 || glyphIndex >= GlyphCount)
        {
            return false;
        }

        var sink = new List<List<OutlinePoint>>(4);
        AppendGlyph(glyphIndex, 0, sink, Transform.Identity);
        if (sink.Count == 0)
        {
            return false;
        }

        outline = new GlyphOutline(sink.ToArray());
        return true;
    }

    private void AppendGlyph(int glyphIndex, int depth, List<List<OutlinePoint>> sink, in Transform transform)
    {
        if (depth > 8 || glyphIndex < 0 || glyphIndex >= GlyphCount)
        {
            return;
        }

        var relativeStart = LocaValue(glyphIndex);
        var relativeEnd = LocaValue(glyphIndex + 1);
        if (relativeStart < 0 || relativeEnd <= relativeStart)
        {
            return;
        }

        var start = _glyf + (int)relativeStart;
        var end = _glyf + (int)relativeEnd;
        if (end > _data.Length || start + 10 > end)
        {
            return;
        }

        var numberOfContours = ReadS16(start);
        var p = start + 10; // 跳过 bbox

        if (numberOfContours >= 0)
        {
            ReadSimpleGlyph(p, end, numberOfContours, sink, transform);
        }
        else
        {
            ReadCompositeGlyph(p, end, depth, sink, transform);
        }
    }

    private void ReadSimpleGlyph(int p, int end, int numberOfContours, List<List<OutlinePoint>> sink, in Transform transform)
    {
        if (numberOfContours == 0)
        {
            return;
        }

        var contourEnds = new int[numberOfContours];
        for (var i = 0; i < numberOfContours; i++, p += 2)
        {
            if (p + 2 > end)
            {
                return;
            }

            contourEnds[i] = ReadU16(p);
        }

        var pointCount = contourEnds[numberOfContours - 1] + 1;
        if (pointCount <= 0 || pointCount > 20000 || p + 2 > end)
        {
            return;
        }

        p += 2 + ReadU16(p); // 跳过指令
        if (p >= end)
        {
            return;
        }

        var flags = new byte[pointCount];
        var filled = 0;
        while (filled < pointCount && p < end)
        {
            var flag = _data[p++];
            flags[filled++] = flag;
            if ((flag & 0x08) != 0 && p < end)
            {
                var repeat = _data[p++];
                for (var r = 0; r < repeat && filled < pointCount; r++)
                {
                    flags[filled++] = flag;
                }
            }
        }

        if (filled < pointCount)
        {
            return;
        }

        var xs = new float[pointCount];
        float x = 0;
        for (var i = 0; i < pointCount; i++)
        {
            var flag = flags[i];
            if ((flag & 0x02) != 0)
            {
                if (p >= end)
                {
                    return;
                }

                var delta = _data[p++];
                x += (flag & 0x10) != 0 ? delta : -delta;
            }
            else if ((flag & 0x10) == 0)
            {
                if (p + 2 > end)
                {
                    return;
                }

                x += ReadS16(p);
                p += 2;
            }

            xs[i] = x;
        }

        var ys = new float[pointCount];
        float y = 0;
        for (var i = 0; i < pointCount; i++)
        {
            var flag = flags[i];
            if ((flag & 0x04) != 0)
            {
                if (p >= end)
                {
                    return;
                }

                var delta = _data[p++];
                y += (flag & 0x20) != 0 ? delta : -delta;
            }
            else if ((flag & 0x20) == 0)
            {
                if (p + 2 > end)
                {
                    return;
                }

                y += ReadS16(p);
                p += 2;
            }

            ys[i] = y;
        }

        var first = 0;
        for (var c = 0; c < numberOfContours; c++)
        {
            var last = contourEnds[c];
            if (last < first || last >= pointCount)
            {
                return;
            }

            var contour = new List<OutlinePoint>(last - first + 1);
            for (var i = first; i <= last; i++)
            {
                contour.Add(transform.Apply(new OutlinePoint(xs[i], ys[i], (flags[i] & 0x01) != 0)));
            }

            sink.Add(contour);
            first = last + 1;
        }
    }

    private void ReadCompositeGlyph(int p, int end, int depth, List<List<OutlinePoint>> sink, in Transform transform)
    {
        int flags;
        do
        {
            if (p + 4 > end)
            {
                return;
            }

            flags = ReadU16(p);
            var componentGlyph = ReadU16(p + 2);
            p += 4;

            float dx;
            float dy;
            if ((flags & Arg1And2AreWords) != 0)
            {
                if (p + 4 > end)
                {
                    return;
                }

                dx = (flags & ArgsAreXyValues) != 0 ? ReadS16(p) : ReadU16(p);
                dy = (flags & ArgsAreXyValues) != 0 ? ReadS16(p + 2) : ReadU16(p + 2);
                p += 4;
            }
            else
            {
                if (p + 2 > end)
                {
                    return;
                }

                dx = (flags & ArgsAreXyValues) != 0 ? (sbyte)_data[p] : _data[p];
                dy = (flags & ArgsAreXyValues) != 0 ? (sbyte)_data[p + 1] : _data[p + 1];
                p += 2;
            }

            float a = 1f, b = 0f, c = 0f, d = 1f;
            if ((flags & HaveScale) != 0)
            {
                a = d = ReadF2Dot14(p);
                p += 2;
            }
            else if ((flags & HaveXYScale) != 0)
            {
                a = ReadF2Dot14(p);
                d = ReadF2Dot14(p + 2);
                p += 4;
            }
            else if ((flags & HaveTwoByTwo) != 0)
            {
                a = ReadF2Dot14(p);
                b = ReadF2Dot14(p + 2);
                c = ReadF2Dot14(p + 4);
                d = ReadF2Dot14(p + 6);
                p += 8;
            }

            AppendGlyph(componentGlyph, depth + 1, sink, transform.Then(new Transform(a, b, c, d, dx, dy)));
        }
        while ((flags & MoreComponents) != 0);
    }
}
