namespace ZlmSharp.Codecs;

/// <summary>RBSP 位读取器（H.264 语法元素解析用），越界抛 <see cref="InvalidDataException"/>。</summary>
public ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPosition;

    public BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPosition = 0;
    }

    public int BitsRemaining => (_data.Length * 8) - _bitPosition;

    public uint ReadBit()
    {
        if (_bitPosition >= _data.Length * 8)
        {
            throw new InvalidDataException("SPS/PPS 数据不足");
        }

        var value = (uint)((_data[_bitPosition >> 3] >> (7 - (_bitPosition & 7))) & 1);
        _bitPosition++;
        return value;
    }

    public bool ReadFlag() => ReadBit() != 0;

    public uint ReadBits(int count)
    {
        if (count is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        uint value = 0;
        for (var i = 0; i < count; i++)
        {
            value = (value << 1) | ReadBit();
        }

        return value;
    }

    /// <summary>无符号指数哥伦布（ue(v)）。</summary>
    public uint ReadUe()
    {
        var leadingZeros = 0;
        while (ReadBit() == 0)
        {
            leadingZeros++;
            if (leadingZeros > 31)
            {
                throw new InvalidDataException("非法 ue(v) 编码");
            }
        }

        if (leadingZeros == 0)
        {
            return 0;
        }

        return (uint)((1L << leadingZeros) - 1 + ReadBits(leadingZeros));
    }

    /// <summary>有符号指数哥伦布（se(v)）。</summary>
    public int ReadSe()
    {
        var code = ReadUe();
        var value = (int)((code + 1) / 2);
        return code % 2 == 0 ? -value : value;
    }

    public void SkipBits(int count)
    {
        if (count < 0 || _bitPosition + count > _data.Length * 8)
        {
            throw new InvalidDataException("SPS/PPS 数据不足");
        }

        _bitPosition += count;
    }
}
