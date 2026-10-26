namespace ZlmSharp.Codecs;

/// <summary>Annex-B 起始码（00 00 01 / 00 00 00 01）工具。</summary>
public static class AnnexB
{
    /// <summary>从 <paramref name="offset"/> 开始查找起始码；同一位置优先识别 4 字节码（index 指向第一个 0）。</summary>
    public static bool TryFindStartCode(ReadOnlySpan<byte> data, int offset, out int index, out int length)
    {
        index = -1;
        length = 0;

        for (var i = Math.Max(0, offset); i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
            {
                continue;
            }

            if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
            {
                index = i;
                length = 4;
                return true;
            }

            if (data[i + 2] == 1)
            {
                index = i;
                length = 3;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从数据开头读取一个 NALU，返回值为下一个起始码的起始位置（无可读数据时为 data.Length）。
    /// </summary>
    public static int ReadNext(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> nalUnit)
    {
        nalUnit = ReadOnlySpan<byte>.Empty;

        if (!TryFindStartCode(data, 0, out var codeIndex, out var codeLength))
        {
            return data.Length;
        }

        var nalStart = codeIndex + codeLength;
        if (nalStart >= data.Length)
        {
            return data.Length;
        }

        var rest = data[nalStart..];
        if (!TryFindStartCode(rest, 0, out var nextIndex, out _))
        {
            nalUnit = TrimTrailingZero(rest);
            return data.Length;
        }

        nalUnit = rest[..nextIndex];
        return nalStart + nextIndex;
    }

    /// <summary>把整段 Annex-B 数据拆成 NALU 列表（会复制数据，适合离线场景）。</summary>
    public static List<byte[]> Split(ReadOnlySpan<byte> data)
    {
        var result = new List<byte[]>();
        var remaining = data;

        while (!remaining.IsEmpty)
        {
            var consumed = ReadNext(remaining, out var nal);
            if (consumed <= 0)
            {
                break;
            }

            if (!nal.IsEmpty)
            {
                result.Add(nal.ToArray());
            }

            if (consumed >= remaining.Length)
            {
                break;
            }

            remaining = remaining[consumed..];
        }

        return result;
    }

    private static ReadOnlySpan<byte> TrimTrailingZero(ReadOnlySpan<byte> nal) =>
        nal.Length > 0 && nal[^1] == 0 ? nal[..^1] : nal;
}
