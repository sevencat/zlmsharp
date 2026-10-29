namespace ZlmSharp.Ai;

/// <summary>已解码的 RGB24 图像，即推理后端的输入（按行排列、可带行跨度）。</summary>
public sealed class RgbImage
{
    public RgbImage(byte[] pixels, int width, int height, int stride = 0)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var rowBytes = checked(width * 3);
        stride = stride <= 0 ? rowBytes : stride;
        if (stride < rowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), "stride 不能小于 width*3");
        }

        var required = (long)stride * (height - 1) + rowBytes;
        if (pixels.Length < required)
        {
            throw new ArgumentException($"像素数据不足：需要 {required} 字节，实际 {pixels.Length}", nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }
}
