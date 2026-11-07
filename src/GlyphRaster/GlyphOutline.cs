namespace GlyphRaster;

/// <summary>
/// 字体单位（em 坐标、y 轴向上）下的一个轮廓点。<see cref="OnCurve"/> 为 false 时是二次贝塞尔控制点。
/// </summary>
public readonly record struct OutlinePoint(float X, float Y, bool OnCurve);

/// <summary>
/// 字形的原始轮廓：若干闭合轮廓，每个轮廓由若干 on/off curve 点组成（TrueType 的二次贝塞尔表示）。
/// </summary>
public sealed class GlyphOutline
{
    /// <summary>空轮廓（空格、缺字）。</summary>
    public static GlyphOutline Empty { get; } = new([]);

    internal GlyphOutline(List<OutlinePoint>[] contours)
    {
        Contours = contours;
    }

    internal List<OutlinePoint>[] Contours { get; }

    public int ContourCount => Contours.Length;

    public bool IsEmpty => Contours.Length == 0;

    public IReadOnlyList<OutlinePoint> GetContour(int index) => Contours[index];
}
