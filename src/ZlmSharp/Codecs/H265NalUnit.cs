namespace ZlmSharp.Codecs;

/// <summary>HEVC NAL 单元类型（ITU-T H.265 Table 7-1）。</summary>
public enum H265NalUnitType
{
    TrailN = 0,
    TrailR = 1,
    TsaN = 2,
    TsaR = 3,
    StsaN = 4,
    StsaR = 5,
    RadlN = 6,
    RadlR = 7,
    RaslN = 8,
    RaslR = 9,
    BlaWLp = 16,
    BlaWRadl = 17,
    BlaNLp = 18,
    IdrWRadl = 19,
    IdrNLp = 20,
    CraNut = 21,
    Vps = 32,
    Sps = 33,
    Pps = 34,
    Aud = 35,
    Eos = 36,
    Eob = 37,
    Fd = 38,
    PrefixSei = 39,
    SuffixSei = 40,
    Ap = 48,
    Fu = 49,
    Papi = 50,
}

/// <summary>HEVC NAL 头（2 字节）解析。</summary>
public static class H265NalUnit
{
    public const int HeaderSize = 2;

    /// <summary>读取 NAL 类型；长度不足或 forbidden_zero_bit 置位时返回 false。</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> nal, out H265NalUnitType type, out int temporalId)
    {
        type = H265NalUnitType.TrailN;
        temporalId = 0;

        if (nal.Length < HeaderSize || (nal[0] & 0x80) != 0)
        {
            return false;
        }

        type = (H265NalUnitType)((nal[0] >> 1) & 0x3F);
        temporalId = (nal[1] & 0x07) - 1;
        return true;
    }

    public static H265NalUnitType GetType(ReadOnlySpan<byte> nal) =>
        nal.Length < HeaderSize
            ? throw new InvalidDataException($"HEVC NAL 太短: {nal.Length}")
            : (H265NalUnitType)((nal[0] >> 1) & 0x3F);

    public static bool IsParameterSet(H265NalUnitType type) =>
        type is H265NalUnitType.Vps or H265NalUnitType.Sps or H265NalUnitType.Pps;

    /// <summary>IRAP 帧（BLA / IDR / CRA 起始的随机接入点）。</summary>
    public static bool IsKeyFrame(H265NalUnitType type) => type is >= H265NalUnitType.BlaWLp and <= H265NalUnitType.CraNut;

    /// <summary>VCL（承载图像数据的 NAL，类型 0~31）。</summary>
    public static bool IsVcl(H265NalUnitType type) => (int)type <= 31;
}
