using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace GlyphRaster;

/// <summary>路径上的一个点（像素坐标，y 轴向下）。</summary>
public readonly record struct PathPoint(float X, float Y);

/// <summary>轴对齐包围盒。</summary>
public readonly record struct PathBounds(float X, float Y, float Width, float Height)
{
    public float Left => X;

    public float Top => Y;

    public float Right => X + Width;

    public float Bottom => Y + Height;

    public float CenterX => X + (Width * 0.5f);

    public float CenterY => Y + (Height * 0.5f);

    /// <summary>零面积（空或退化）包围盒。</summary>
    public bool IsEmpty => Width <= 0f || Height <= 0f;

    public static PathBounds FromLtrb(float left, float top, float right, float bottom)
        => new(left, top, right - left, bottom - top);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"({X:0.###}, {Y:0.###}, {Width:0.###} x {Height:0.###})");
}

/// <summary>
/// 一条子路径，对应字形的一个闭合轮廓（如「回」有内外两条）。
/// 段序列不含起始 MoveTo、不含结尾 Close；闭合状态由 <see cref="IsClosed"/> 表示。
/// </summary>
public sealed class VectorPath
{
    private readonly PathSegment[] _segments;

    public VectorPath(PathPoint start, IReadOnlyList<PathSegment> segments, bool isClosed = true)
    {
        ArgumentNullException.ThrowIfNull(segments);

        Start = start;
        _segments = segments as PathSegment[] ?? [.. segments];
        IsClosed = isClosed;
        Bounds = ComputeBounds();
    }

    /// <summary>子路径起点。</summary>
    public PathPoint Start { get; }

    /// <summary>直线段与二次贝塞尔段（顺序绘制，终点接终点）。</summary>
    public IReadOnlyList<PathSegment> Segments => _segments;

    /// <summary>是否闭合。</summary>
    public bool IsClosed { get; }

    /// <summary>包围盒（取控制点凸包，与 <see cref="GlyphPath"/> 的口径一致）。</summary>
    public PathBounds Bounds { get; }

    /// <summary>没有任何段。</summary>
    public bool IsEmpty => _segments.Length == 0;

    /// <summary>应用仿射变换，返回新路径。</summary>
    public VectorPath Transform(Matrix3x2 matrix)
    {
        var start = Vector2.Transform(new Vector2(Start.X, Start.Y), matrix);
        var segments = new PathSegment[_segments.Length];

        for (var i = 0; i < _segments.Length; i++)
        {
            var segment = _segments[i];
            var end = Vector2.Transform(new Vector2(segment.X2, segment.Y2), matrix);
            if (segment.Verb == PathVerb.QuadraticTo)
            {
                var control = Vector2.Transform(new Vector2(segment.X1, segment.Y1), matrix);
                segments[i] = new PathSegment(segment.Verb, control.X, control.Y, end.X, end.Y);
            }
            else
            {
                segments[i] = new PathSegment(segment.Verb, 0f, 0f, end.X, end.Y);
            }
        }

        return new VectorPath(new PathPoint(start.X, start.Y), segments, IsClosed);
    }

    /// <summary>返回闭合版本。</summary>
    public VectorPath AsClosed() => IsClosed ? this : new VectorPath(Start, _segments, true);

    /// <summary>
    /// 把二次贝塞尔细分成折线，供只接受多边形的绘制端使用。
    /// <paramref name="tolerance"/> 是允许的最大弦高误差（像素）。
    /// </summary>
    public VectorPath Flatten(float tolerance = 0.25f)
    {
        if (tolerance <= 0f)
        {
            tolerance = 0.25f;
        }

        if (_segments.Length == 0)
        {
            return this;
        }

        var result = new List<PathSegment>(_segments.Length * 3);
        var currentX = Start.X;
        var currentY = Start.Y;

        foreach (var segment in _segments)
        {
            if (segment.Verb == PathVerb.QuadraticTo)
            {
                var midX = (currentX + segment.X2) * 0.5f;
                var midY = (currentY + segment.Y2) * 0.5f;
                var ex = segment.X1 - midX;
                var ey = segment.Y1 - midY;
                var deviation = MathF.Sqrt((ex * ex) + (ey * ey));
                var steps = (int)MathF.Ceiling(MathF.Sqrt(deviation / (4f * tolerance)));
                steps = Math.Clamp(steps, 1, 128);

                for (var step = 1; step <= steps; step++)
                {
                    var t = (float)step / steps;
                    var mt = 1f - t;
                    var a = mt * mt;
                    var b = 2f * mt * t;
                    var c = t * t;
                    result.Add(new PathSegment(
                        PathVerb.LineTo,
                        0f,
                        0f,
                        (a * currentX) + (b * segment.X1) + (c * segment.X2),
                        (a * currentY) + (b * segment.Y1) + (c * segment.Y2)));
                }
            }
            else if (segment.Verb == PathVerb.LineTo)
            {
                result.Add(segment);
            }

            currentX = segment.X2;
            currentY = segment.Y2;
        }

        return new VectorPath(Start, result, IsClosed);
    }

    /// <summary>输出单条子路径的 SVG path data（<c>d</c> 属性）。</summary>
    public string ToSvgPathData()
    {
        var builder = new StringBuilder(32 + (_segments.Length * 18));
        builder.Append('M').Append(' ').Append(Format(Start.X)).Append(' ').Append(Format(Start.Y));

        foreach (var segment in _segments)
        {
            if (segment.Verb == PathVerb.LineTo)
            {
                builder.Append(" L ").Append(Format(segment.X2)).Append(' ').Append(Format(segment.Y2));
            }
            else if (segment.Verb == PathVerb.QuadraticTo)
            {
                builder.Append(" Q ")
                    .Append(Format(segment.X1)).Append(' ')
                    .Append(Format(segment.Y1)).Append(' ')
                    .Append(Format(segment.X2)).Append(' ')
                    .Append(Format(segment.Y2));
            }
        }

        if (IsClosed)
        {
            builder.Append(" Z");
        }

        return builder.ToString();
    }

    public override string ToString() => ToSvgPathData();

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private PathBounds ComputeBounds()
    {
        var minX = Start.X;
        var minY = Start.Y;
        var maxX = Start.X;
        var maxY = Start.Y;

        foreach (var segment in _segments)
        {
            if (segment.Verb == PathVerb.QuadraticTo)
            {
                Track(segment.X1, segment.Y1);
            }

            Track(segment.X2, segment.Y2);
        }

        return PathBounds.FromLtrb(minX, minY, maxX, maxY);

        void Track(float x, float y)
        {
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
}

/// <summary>
/// 一组矢量路径，语义对齐 SixLabors.Fonts 的 <c>IPathCollection</c>：
/// 可枚举每一条闭合子路径、可整体变换、可取包围盒、可导出 SVG path data。
/// 由 <see cref="GlyphPath.ToPathCollection"/> / <see cref="TextFont.GetGlyphPaths"/> /
/// <see cref="TextFont.GetTextPaths"/> 产生。
/// </summary>
public sealed class PathCollection : IReadOnlyList<VectorPath>
{
    private readonly VectorPath[] _paths;

    public PathCollection(IReadOnlyList<VectorPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths as VectorPath[] ?? [.. paths];
        Bounds = ComputeBounds(_paths);
    }

    /// <summary>空集合。</summary>
    public static PathCollection Empty { get; } = new([]);

    public int Count => _paths.Length;

    public VectorPath this[int index] => _paths[index];

    /// <summary>所有子路径的合并包围盒；空集合时为 <c>default</c>。</summary>
    public PathBounds Bounds { get; }

    /// <summary>是否没有任何子路径。</summary>
    public bool IsEmpty => _paths.Length == 0;

    /// <summary>整体应用仿射变换（平移 <see cref="Matrix3x2.CreateTranslation(float, float)"/>、缩放、旋转均可）。</summary>
    public PathCollection Transform(Matrix3x2 matrix)
    {
        if (_paths.Length == 0)
        {
            return this;
        }

        var result = new VectorPath[_paths.Length];
        for (var i = 0; i < _paths.Length; i++)
        {
            result[i] = _paths[i].Transform(matrix);
        }

        return new PathCollection(result);
    }

    /// <summary>把所有子路径标为闭合。</summary>
    public PathCollection AsClosedPath()
    {
        if (_paths.Length == 0)
        {
            return this;
        }

        var result = new VectorPath[_paths.Length];
        for (var i = 0; i < _paths.Length; i++)
        {
            result[i] = _paths[i].AsClosed();
        }

        return new PathCollection(result);
    }

    /// <summary>把所有二次贝塞尔细分成折线。</summary>
    public PathCollection Flatten(float tolerance = 0.25f)
    {
        if (_paths.Length == 0)
        {
            return this;
        }

        var result = new VectorPath[_paths.Length];
        for (var i = 0; i < _paths.Length; i++)
        {
            result[i] = _paths[i].Flatten(tolerance);
        }

        return new PathCollection(result);
    }

    /// <summary>合并导出为单条 SVG path data（多条子路径用 <c>M</c> 分隔）。</summary>
    public string ToSvgPathData()
    {
        if (_paths.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(_paths.Length * 256);
        foreach (var path in _paths)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(path.ToSvgPathData());
        }

        return builder.ToString();
    }

    public IEnumerator<VectorPath> GetEnumerator() => ((IEnumerable<VectorPath>)_paths).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _paths.GetEnumerator();

    public override string ToString() => $"PathCollection({_paths.Length} paths, {Bounds})";

    private static PathBounds ComputeBounds(VectorPath[] paths)
    {
        if (paths.Length == 0)
        {
            return default;
        }

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;

        foreach (var path in paths)
        {
            var bounds = path.Bounds;
            if (bounds.X < minX)
            {
                minX = bounds.X;
            }

            if (bounds.Y < minY)
            {
                minY = bounds.Y;
            }

            if (bounds.X + bounds.Width > maxX)
            {
                maxX = bounds.X + bounds.Width;
            }

            if (bounds.Y + bounds.Height > maxY)
            {
                maxY = bounds.Y + bounds.Height;
            }
        }

        return PathBounds.FromLtrb(minX, minY, maxX, maxY);
    }
}
