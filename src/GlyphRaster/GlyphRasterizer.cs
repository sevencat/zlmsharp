namespace GlyphRaster;

/// <summary>
/// 把字体单位的字形轮廓扫成 A8 覆盖位图。
/// 做法：二次贝塞尔展平成折线 → 每条像素行取若干子扫描线 → 非零环绕规则求区间 →
/// 区间与像素的横向交叠长度累加成覆盖率。纵向超采样 + 横向精确覆盖，等效 4x AA。
/// </summary>
internal static class GlyphRasterizer
{
    private const int SubSamples = 4;
    private const int MaxMaskSize = 4096;

    public static GlyphMask Rasterize(GlyphOutline outline, float scale, int advance)
    {
        if (outline.IsEmpty || scale <= 0f)
        {
            return GlyphMask.Blank(advance);
        }

        var polygons = BuildPolygons(outline, scale);
        if (polygons.Count == 0)
        {
            return GlyphMask.Blank(advance);
        }

        var (minX, minY, maxX, maxY) = GetBounds(polygons);
        if (maxX - minX < 0.01f || maxY - minY < 0.01f)
        {
            return GlyphMask.Blank(advance);
        }

        var left = (int)MathF.Floor(minX);
        var top = (int)MathF.Floor(minY);
        var width = (int)MathF.Ceiling(maxX) - left;
        var height = (int)MathF.Ceiling(maxY) - top;
        if (width <= 0 || height <= 0 || width > MaxMaskSize || height > MaxMaskSize)
        {
            return GlyphMask.Blank(advance);
        }

        var coverage = new byte[width * height];
        var row = new float[width];
        var crossings = new List<(float X, int Winding)>(64);

        for (var y = 0; y < height; y++)
        {
            Array.Clear(row);
            for (var s = 0; s < SubSamples; s++)
            {
                var sampleY = top + y + ((s + 0.5f) / SubSamples);
                crossings.Clear();
                foreach (var polygon in polygons)
                {
                    CollectCrossings(polygon, sampleY, crossings);
                }

                if (crossings.Count < 2)
                {
                    continue;
                }

                crossings.Sort(static (a, b) => a.X.CompareTo(b.X));
                var winding = 0;
                var spanStart = 0f;
                foreach (var (x, w) in crossings)
                {
                    if (winding != 0)
                    {
                        AddSpan(row, left, spanStart, x);
                    }

                    winding += w;
                    spanStart = x;
                }
            }

            var rowBase = y * width;
            for (var i = 0; i < width; i++)
            {
                var value = row[i] / SubSamples;
                if (value > 1f)
                {
                    value = 1f;
                }
                else if (value < 0f)
                {
                    value = 0f;
                }

                coverage[rowBase + i] = (byte)((value * 255f) + 0.5f);
            }
        }

        return new GlyphMask(width, height, left, top, coverage, advance);
    }

    private static List<float[]> BuildPolygons(GlyphOutline outline, float scale)
    {
        var polygons = new List<float[]>(outline.ContourCount);
        for (var c = 0; c < outline.ContourCount; c++)
        {
            var points = outline.Contours[c];
            if (points.Count < 2)
            {
                continue;
            }

            var xy = new List<float>(points.Count * 3);
            BuildPolygon(points, scale, xy);
            if (xy.Count >= 6)
            {
                polygons.Add(xy.ToArray());
            }
        }

        return polygons;
    }

    /// <summary>把 on/off-curve 点序列转成闭合折线（像素坐标，y 向下）。</summary>
    private static void BuildPolygon(List<OutlinePoint> points, float scale, List<float> xy)
    {
        var count = points.Count;
        var startIndex = -1;
        for (var i = 0; i < count; i++)
        {
            if (points[i].OnCurve)
            {
                startIndex = i;
                break;
            }
        }

        float startX;
        float startY;
        int first;
        int length;
        if (startIndex >= 0)
        {
            startX = points[startIndex].X * scale;
            startY = -points[startIndex].Y * scale;
            first = (startIndex + 1) % count;
            length = count - 1;
        }
        else
        {
            // 整条轮廓都是控制点：以首尾中点为起点
            startX = (points[count - 1].X + points[0].X) * 0.5f * scale;
            startY = -(points[count - 1].Y + points[0].Y) * 0.5f * scale;
            first = 0;
            length = count;
        }

        xy.Add(startX);
        xy.Add(startY);

        var currentX = startX;
        var currentY = startY;
        var controlX = 0f;
        var controlY = 0f;
        var hasControl = false;

        for (var k = 0; k < length; k++)
        {
            var point = points[(first + k) % count];
            var x = point.X * scale;
            var y = -point.Y * scale;
            if (point.OnCurve)
            {
                if (hasControl)
                {
                    AddQuadratic(xy, currentX, currentY, controlX, controlY, x, y);
                    hasControl = false;
                }
                else
                {
                    xy.Add(x);
                    xy.Add(y);
                }

                currentX = x;
                currentY = y;
            }
            else
            {
                if (hasControl)
                {
                    var midX = (controlX + x) * 0.5f;
                    var midY = (controlY + y) * 0.5f;
                    AddQuadratic(xy, currentX, currentY, controlX, controlY, midX, midY);
                    currentX = midX;
                    currentY = midY;
                }

                controlX = x;
                controlY = y;
                hasControl = true;
            }
        }

        if (hasControl)
        {
            AddQuadratic(xy, currentX, currentY, controlX, controlY, startX, startY);
        }
        else
        {
            xy.Add(startX);
            xy.Add(startY);
        }
    }

    private static void AddQuadratic(List<float> xy, float x0, float y0, float cx, float cy, float x1, float y1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var chord = MathF.Sqrt((dx * dx) + (dy * dy));
        var ex = cx - ((x0 + x1) * 0.5f);
        var ey = cy - ((y0 + y1) * 0.5f);
        var deviation = MathF.Sqrt((ex * ex) + (ey * ey));

        var steps = (int)MathF.Ceiling(MathF.Sqrt((deviation * 8f) + 0.5f));
        if (chord < 1f && steps < 1)
        {
            steps = 1;
        }

        steps = Math.Clamp(steps, 1, 32);

        for (var i = 1; i <= steps; i++)
        {
            var t = (float)i / steps;
            var mt = 1f - t;
            var a = mt * mt;
            var b = 2f * mt * t;
            var c = t * t;
            xy.Add((a * x0) + (b * cx) + (c * x1));
            xy.Add((a * y0) + (b * cy) + (c * y1));
        }
    }

    private static (float MinX, float MinY, float MaxX, float MaxY) GetBounds(List<float[]> polygons)
    {
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        foreach (var polygon in polygons)
        {
            for (var i = 0; i + 1 < polygon.Length; i += 2)
            {
                var x = polygon[i];
                var y = polygon[i + 1];
                if (x < minX)
                {
                    minX = x;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (y > maxY)
                {
                    maxY = y;
                }
            }
        }

        return (minX, minY, maxX, maxY);
    }

    private static void CollectCrossings(float[] polygon, float sampleY, List<(float X, int Winding)> crossings)
    {
        var count = polygon.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var j = (i + 1) % count;
            var y0 = polygon[(i * 2) + 1];
            var y1 = polygon[(j * 2) + 1];
            if (y0 == y1)
            {
                continue;
            }

            // 半开区间，避免顶点被重复计数
            if (!((y0 <= sampleY && y1 > sampleY) || (y1 <= sampleY && y0 > sampleY)))
            {
                continue;
            }

            var x0 = polygon[i * 2];
            var x1 = polygon[j * 2];
            var t = (sampleY - y0) / (y1 - y0);
            crossings.Add((x0 + (t * (x1 - x0)), y0 < y1 ? 1 : -1));
        }
    }

    private static void AddSpan(float[] row, int left, float spanStart, float spanEnd)
    {
        if (spanEnd <= spanStart)
        {
            return;
        }

        var start = spanStart - left;
        var end = spanEnd - left;
        var firstColumn = (int)MathF.Floor(start);
        var lastColumn = (int)MathF.Ceiling(end);
        if (firstColumn < 0)
        {
            firstColumn = 0;
        }

        if (lastColumn > row.Length)
        {
            lastColumn = row.Length;
        }

        for (var i = firstColumn; i < lastColumn; i++)
        {
            var a = MathF.Max(start, i);
            var b = MathF.Min(end, i + 1);
            if (b > a)
            {
                row[i] += b - a;
            }
        }
    }
}
