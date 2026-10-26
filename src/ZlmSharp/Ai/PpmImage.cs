using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace ZlmSharp.Ai;

/// <summary>
/// P6（二进制 RGB）PPM 解析。选它做图片中转格式是因为零依赖：
/// ffmpeg 能直接输出 PPM（<c>-f image2pipe -vcodec ppm</c>），库本身只需要 RGB24。
/// </summary>
public static class PpmImage
{
    private const int MaxDimension = 16384;

    /// <summary>解析 P6 PPM；失败时 <paramref name="error"/> 给出中文原因。</summary>
    public static bool TryParse(
        ReadOnlySpan<byte> data,
        [NotNullWhen(true)] out RgbImage? image,
        [NotNullWhen(false)] out string? error)
    {
        image = null;
        error = null;
        var index = 0;

        if (!TryReadToken(data, ref index, out var magic))
        {
            error = "PPM 头不完整";
            return false;
        }

        if (!string.Equals(magic, "P6", StringComparison.Ordinal))
        {
            error = $"只支持 P6（二进制 RGB）PPM，实际为 {magic}";
            return false;
        }

        if (!TryReadToken(data, ref index, out var widthText) ||
            !int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            width is < 1 or > MaxDimension)
        {
            error = "PPM 宽度非法";
            return false;
        }

        if (!TryReadToken(data, ref index, out var heightText) ||
            !int.TryParse(heightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            height is < 1 or > MaxDimension)
        {
            error = "PPM 高度非法";
            return false;
        }

        if (!TryReadToken(data, ref index, out var maxValueText) ||
            !int.TryParse(maxValueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxValue))
        {
            error = "PPM maxval 非法";
            return false;
        }

        if (maxValue != 255)
        {
            error = $"只支持 maxval=255 的 PPM，实际为 {maxValue}";
            return false;
        }

        // maxval 之后固定一个空白分隔符（通常是 \n，也可能是 \r\n）
        if (index >= data.Length)
        {
            error = "PPM 像素数据缺失";
            return false;
        }

        index += data[index] == '\r' && index + 1 < data.Length && data[index + 1] == '\n' ? 2 : 1;

        var required = (long)width * height * 3;
        if (data.Length - index < required)
        {
            error = $"PPM 像素数据不足：需要 {required} 字节，实际 {data.Length - index}";
            return false;
        }

        image = new RgbImage(data.Slice(index, (int)required).ToArray(), width, height);
        return true;
    }

    private static bool TryReadToken(ReadOnlySpan<byte> data, ref int index, out string token)
    {
        token = string.Empty;

        while (index < data.Length)
        {
            if (data[index] == '#')
            {
                while (index < data.Length && data[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (IsWhiteSpace(data[index]))
            {
                index++;
                continue;
            }

            break;
        }

        var start = index;
        while (index < data.Length && !IsWhiteSpace(data[index]) && data[index] != '#')
        {
            index++;
        }

        if (index == start)
        {
            return false;
        }

        token = Encoding.ASCII.GetString(data[start..index]);
        return true;
    }

    private static bool IsWhiteSpace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0B or 0x0C;
}
