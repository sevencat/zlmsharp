namespace ZlmSharp.Codecs;

/// <summary>H.264 NAL 单元类型（ITU-T H.264 Table 7-1）。</summary>
public enum H264NalUnitType
{
    Unspecified = 0,
    Slice = 1,
    SliceDataPartitionA = 2,
    SliceDataPartitionB = 3,
    SliceDataPartitionC = 4,
    IdrSlice = 5,
    Sei = 6,
    Sps = 7,
    Pps = 8,
    AccessUnitDelimiter = 9,
    EndOfSequence = 10,
    EndOfStream = 11,
    Filler = 12,
    SpsExtension = 13,
    PrefixNalUnit = 14,
    SubsetSps = 15,
    AuxiliarySlice = 19,
}

/// <summary>NAL 单元首字节解析工具。</summary>
public static class H264NalUnit
{
    /// <summary>取 NAL 类型；空数据返回 <see cref="H264NalUnitType.Unspecified"/>。</summary>
    public static H264NalUnitType GetType(ReadOnlySpan<byte> nalUnit) =>
        nalUnit.IsEmpty ? H264NalUnitType.Unspecified : (H264NalUnitType)(nalUnit[0] & 0x1F);

    public static bool IsVcl(ReadOnlySpan<byte> nalUnit)
    {
        var type = GetType(nalUnit);
        return type is >= H264NalUnitType.Slice and <= H264NalUnitType.IdrSlice;
    }

    public static bool IsKeyFrame(ReadOnlySpan<byte> nalUnit) => GetType(nalUnit) == H264NalUnitType.IdrSlice;

    public static bool IsConfig(ReadOnlySpan<byte> nalUnit) =>
        GetType(nalUnit) is H264NalUnitType.Sps or H264NalUnitType.Pps;

    public static bool IsAud(ReadOnlySpan<byte> nalUnit) => GetType(nalUnit) == H264NalUnitType.AccessUnitDelimiter;

    public static bool IsSei(ReadOnlySpan<byte> nalUnit) => GetType(nalUnit) == H264NalUnitType.Sei;

    /// <summary>从 Annex-B 字节流中判断是否包含 IDR（用于整帧判定）。</summary>
    public static bool ContainsIdrAnnexB(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var consumed = AnnexB.ReadNext(data, out var nal);
            if (consumed <= 0)
            {
                break;
            }

            if (IsKeyFrame(nal))
            {
                return true;
            }

            data = data[consumed..];
        }

        return false;
    }
}
