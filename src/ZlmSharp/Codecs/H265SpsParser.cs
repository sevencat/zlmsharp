namespace ZlmSharp.Codecs;

/// <summary>HEVC SPS 解析结果（只取转发/封装需要的字段）。</summary>
public sealed record H265SpsInfo(
    int VpsId,
    int MaxSubLayersMinus1,
    int ProfileSpace,
    int TierFlag,
    int ProfileIdc,
    uint ProfileCompatibilityFlags,
    ulong ConstraintFlags,
    int LevelIdc,
    int ChromaFormatIdc,
    int Width,
    int Height,
    int BitDepthLuma,
    int BitDepthChroma,
    int SpsId,
    bool TemporalIdNested)
{
    public string ProfileName => ProfileIdc switch
    {
        1 => "Main",
        2 => "Main10",
        3 => "MainStillPicture",
        4 => "Rext",
        _ => $"Profile{ProfileIdc}",
    };
}

/// <summary>
/// HEVC SPS 解析：profile_tier_level（含 48 位 constraint flags）与 conformance window 裁剪，
/// 足以覆盖 x265 / 常见 IPC 的 Main / Main10。
/// </summary>
public static class H265SpsParser
{
    public static bool TryParse(ReadOnlySpan<byte> spsNal, out H265SpsInfo? info, out string error)
    {
        info = null;
        error = string.Empty;

        try
        {
            info = Parse(spsNal);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException)
        {
            error = exception.Message;
            return false;
        }
    }

    public static H265SpsInfo Parse(ReadOnlySpan<byte> spsNal)
    {
        if (spsNal.Length < 12)
        {
            throw new InvalidDataException($"HEVC SPS 太短: {spsNal.Length}");
        }

        if (H265NalUnit.GetType(spsNal) != H265NalUnitType.Sps)
        {
            throw new InvalidDataException($"不是 HEVC SPS NALU（type={(int)H265NalUnit.GetType(spsNal)}）");
        }

        var rbsp = H264SpsParser.ToRbsp(spsNal[2..]);
        var reader = new BitReader(rbsp);

        var vpsId = (int)reader.ReadBits(4);
        var maxSubLayersMinus1 = (int)reader.ReadBits(3);
        var temporalIdNested = reader.ReadFlag();

        var (profileSpace, tier, profileIdc, compatFlags, constraintFlags, levelIdc) =
            ParseProfileTierLevel(ref reader);

        // sub_layer_profile_present_flag / sub_layer_level_present_flag
        var subLayerProfile = new bool[maxSubLayersMinus1];
        var subLayerLevel = new bool[maxSubLayersMinus1];
        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            subLayerProfile[i] = reader.ReadFlag();
            subLayerLevel[i] = reader.ReadFlag();
        }

        if (maxSubLayersMinus1 > 0)
        {
            for (var i = maxSubLayersMinus1; i < 8; i++)
            {
                reader.SkipBits(2); // reserved_zero_2bits
            }
        }

        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            if (subLayerProfile[i])
            {
                SkipProfileTierLevel(ref reader);
            }

            if (subLayerLevel[i])
            {
                reader.SkipBits(8); // sub_layer_level_idc
            }
        }

        var spsId = (int)reader.ReadUe();
        var chromaFormatIdc = (int)reader.ReadUe();
        var separateColourPlane = false;
        if (chromaFormatIdc == 3)
        {
            separateColourPlane = reader.ReadFlag();
        }

        var picWidth = (int)reader.ReadUe();
        var picHeight = (int)reader.ReadUe();

        var cropLeft = 0;
        var cropRight = 0;
        var cropTop = 0;
        var cropBottom = 0;
        if (reader.ReadFlag()) // conformance_window_flag
        {
            cropLeft = (int)reader.ReadUe();
            cropRight = (int)reader.ReadUe();
            cropTop = (int)reader.ReadUe();
            cropBottom = (int)reader.ReadUe();
        }

        var bitDepthLuma = (int)reader.ReadUe() + 8;
        var bitDepthChroma = (int)reader.ReadUe() + 8;

        var (subWidthC, subHeightC) = GetChromaSubSampling(chromaFormatIdc, separateColourPlane);
        var width = picWidth - (cropLeft + cropRight) * subWidthC;
        var height = picHeight - (cropTop + cropBottom) * subHeightC;

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"HEVC SPS 解析出的分辨率非法: {width}x{height}");
        }

        return new H265SpsInfo(
            vpsId,
            maxSubLayersMinus1,
            profileSpace,
            tier,
            profileIdc,
            compatFlags,
            constraintFlags,
            levelIdc,
            chromaFormatIdc,
            width,
            height,
            bitDepthLuma,
            bitDepthChroma,
            spsId,
            temporalIdNested);
    }

    private static (int ProfileSpace, int Tier, int ProfileIdc, uint CompatFlags, ulong ConstraintFlags, int LevelIdc)
        ParseProfileTierLevel(ref BitReader reader)
    {
        var profileSpace = (int)reader.ReadBits(2);
        var tier = (int)reader.ReadBit();
        var profileIdc = (int)reader.ReadBits(5);

        uint compatFlags = 0;
        for (var i = 0; i < 32; i++)
        {
            compatFlags = (compatFlags << 1) | reader.ReadBit();
        }

        // 4 个 general_*_constraint_flag + 44 位保留/约束位 = 48 位
        ulong constraintFlags = 0;
        for (var i = 0; i < 48; i++)
        {
            constraintFlags = (constraintFlags << 1) | reader.ReadBit();
        }

        var levelIdc = (int)reader.ReadBits(8);
        return (profileSpace, tier, profileIdc, compatFlags, constraintFlags, levelIdc);
    }

    private static void SkipProfileTierLevel(ref BitReader reader)
    {
        reader.SkipBits(2 + 1 + 5 + 32 + 48 + 8);
    }

    private static (int SubWidthC, int SubHeightC) GetChromaSubSampling(int chromaFormatIdc, bool separateColourPlane)
    {
        if (separateColourPlane || chromaFormatIdc == 0)
        {
            return (1, 1);
        }

        return chromaFormatIdc switch
        {
            1 => (2, 2),
            2 => (2, 1),
            _ => (1, 1),
        };
    }
}
