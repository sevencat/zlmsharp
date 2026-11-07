namespace GlyphRaster;

/// <summary>路径段类型。</summary>
public enum PathVerb
{
    /// <summary>开始新的子路径，终点在 (X2, Y2)。</summary>
    MoveTo,

    /// <summary>直线到 (X2, Y2)。</summary>
    LineTo,

    /// <summary>二次贝塞尔到 (X2, Y2)，控制点 (X1, Y1)。</summary>
    QuadraticTo,

    /// <summary>闭合当前子路径。</summary>
    Close,
}

/// <summary>一段路径。未使用的坐标字段为 0。</summary>
public readonly record struct PathSegment(PathVerb Verb, float X1, float Y1, float X2, float Y2);

/// <summary>
/// 字形轮廓的矢量化表示（已按字号缩放、y 轴向下、原点是笔位在基线上的位置）。
/// 调用方可以自己用 GDI/Skia/SVG/GPU 去画，不经过本库的光栅化器。
/// </summary>
public sealed class GlyphPath
{
    public GlyphPath(IReadOnlyList<PathSegment> segments, float minX, float minY, float maxX, float maxY)
    {
        Segments = segments;
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    public IReadOnlyList<PathSegment> Segments { get; }

    public float MinX { get; }

    public float MinY { get; }

    public float MaxX { get; }

    public float MaxY { get; }

    public float Width => MaxX - MinX;

    public float Height => MaxY - MinY;

    public bool IsEmpty => Segments.Count == 0;

    /// <summary>
    /// 按轮廓切分成路径集合：每个 <see cref="PathVerb.MoveTo"/> 到 <see cref="PathVerb.Close"/> 为一条
    /// <see cref="VectorPath"/>。返回结果可整体变换、取包围盒、导出 SVG。
    /// </summary>
    public PathCollection ToPathCollection()
    {
        if (Segments.Count == 0)
        {
            return PathCollection.Empty;
        }

        var paths = new List<VectorPath>(4);
        var current = new List<PathSegment>(32);
        var start = default(PathPoint);
        var opened = false;
        var isClosed = false;

        foreach (var segment in Segments)
        {
            switch (segment.Verb)
            {
                case PathVerb.MoveTo:
                    Flush();
                    start = new PathPoint(segment.X2, segment.Y2);
                    opened = true;
                    isClosed = false;
                    break;

                case PathVerb.Close:
                    isClosed = true;
                    Flush();
                    break;

                default:
                    current.Add(segment);
                    break;
            }
        }

        Flush();
        return paths.Count == 0 ? PathCollection.Empty : new PathCollection(paths);

        void Flush()
        {
            if (!opened)
            {
                return;
            }

            if (current.Count > 0)
            {
                paths.Add(new VectorPath(start, current.ToArray(), isClosed));
            }

            current.Clear();
            opened = false;
            isClosed = false;
        }
    }
}
