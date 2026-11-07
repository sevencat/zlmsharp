namespace GlyphRaster;

/// <summary>
/// 单个字形光栅化后的 A8 覆盖位图（每个字节 0..255 表示该像素被字形覆盖的比例）。
/// </summary>
/// <remarks>
/// 坐标约定：<see cref="OffsetX"/> 是位图左边界相对当前笔位（pen）的像素偏移，
/// <see cref="OffsetY"/> 是位图顶边界相对基线（baseline）的像素偏移，向下为正。
/// 因此绘制位置 = (penX + OffsetX, baselineY + OffsetY)。
/// </remarks>
public sealed class GlyphMask
{
    /// <summary>空白字形（空格、缺字）：没有像素，只有前进量。</summary>
    public static GlyphMask Blank(int advance) => new(0, 0, 0, 0, [], advance);

    public GlyphMask(int width, int height, int offsetX, int offsetY, byte[] coverage, int advance)
    {
        Width = width;
        Height = height;
        OffsetX = offsetX;
        OffsetY = offsetY;
        Coverage = coverage;
        Advance = advance;
    }

    /// <summary>位图宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>位图高度（像素）。</summary>
    public int Height { get; }

    /// <summary>位图左边界相对笔位的像素偏移（通常为负，表示字形在笔位左侧伸出）。</summary>
    public int OffsetX { get; }

    /// <summary>位图顶边界相对基线的像素偏移（通常为负，表示字身在基线上方）。</summary>
    public int OffsetY { get; }

    /// <summary><c>Height</c> 行 × <c>Width</c> 列的 A8 覆盖数据，行优先。</summary>
    public byte[] Coverage { get; }

    /// <summary>绘制该字形后笔位前进的像素数。</summary>
    public int Advance { get; }

    /// <summary>是否没有任何可见像素。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0 || Coverage.Length == 0;
}
