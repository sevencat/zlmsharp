namespace ZlmSharp.Codecs;

/// <summary>SPS 解析结果（一期只取转发/封装需要的字段）。</summary>
public sealed record H264SpsInfo(
    int SpsId,
    int ProfileIdc,
    int LevelIdc,
    int ChromaFormatIdc,
    int Width,
    int Height,
    int MaxNumRefFrames,
    bool FrameMbsOnly,
    int ConstraintFlags,
    bool HasVui)
{
    /// <summary>常用 profile 名称，便于日志与 API 展示。</summary>
    public string ProfileName => ProfileIdc switch
    {
        66 => "Baseline",
        77 => "Main",
        88 => "Extended",
        100 => "High",
        110 => "High10",
        122 => "High422",
        244 => "High444",
        _ => $"Profile{ProfileIdc}",
    };
}

/// <summary>
/// H.264 SPS 解析：支持 emulation prevention 去除、chroma_format、frame cropping，
/// 足以覆盖 x264/ffmpeg 与常见 IPC 的 profile（Baseline/Main/High/High444）。
/// </summary>
public static class H264SpsParser
{
    public static bool TryParse(ReadOnlySpan<byte> spsNal, out H264SpsInfo? info, out string error)
    {
        info = null;
        error = string.Empty;

        try
        {
            info = Parse(spsNal);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static H264SpsInfo Parse(ReadOnlySpan<byte> spsNal)
    {
        if (spsNal.Length < 4)
        {
            throw new InvalidDataException($"SPS 太短: {spsNal.Length}");
        }

        if (H264NalUnit.GetType(spsNal) != H264NalUnitType.Sps)
        {
            throw new InvalidDataException($"不是 SPS NALU（type={(int)H264NalUnit.GetType(spsNal)}）");
        }

        var rbsp = ToRbsp(spsNal[1..]);
        var reader = new BitReader(rbsp);

        var profileIdc = (int)reader.ReadBits(8);
        var constraintFlags = (int)reader.ReadBits(8);
        var levelIdc = (int)reader.ReadBits(8);
        var spsId = (int)reader.ReadUe();

        var chromaFormatIdc = 1;
        var separateColourPlane = false;

        if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chromaFormatIdc = (int)reader.ReadUe();
            if (chromaFormatIdc == 3)
            {
                separateColourPlane = reader.ReadFlag();
            }

            _ = reader.ReadUe(); // bit_depth_luma_minus8
            _ = reader.ReadUe(); // bit_depth_chroma_minus8
            reader.SkipBits(1);  // qpprime_y_zero_transform_bypass_flag

            if (reader.ReadFlag()) // seq_scaling_matrix_present_flag
            {
                var count = chromaFormatIdc != 3 ? 8 : 12;
                for (var i = 0; i < count; i++)
                {
                    if (reader.ReadFlag())
                    {
                        SkipScalingList(ref reader, i < 6 ? 16 : 64);
                    }
                }
            }
        }

        _ = reader.ReadUe(); // log2_max_frame_num_minus4
        var picOrderCntType = (int)reader.ReadUe();
        if (picOrderCntType == 0)
        {
            _ = reader.ReadUe(); // log2_max_pic_order_cnt_lsb_minus4
        }
        else if (picOrderCntType == 1)
        {
            reader.SkipBits(1); // delta_pic_order_always_zero_flag
            _ = reader.ReadSe(); // offset_for_non_ref_pic
            _ = reader.ReadSe(); // offset_for_top_to_bottom_field
            var cycleLength = (int)reader.ReadUe();
            for (var i = 0; i < cycleLength; i++)
            {
                _ = reader.ReadSe();
            }
        }

        var maxNumRefFrames = (int)reader.ReadUe();
        reader.SkipBits(1); // gaps_in_frame_num_value_allowed_flag

        var picWidthInMbsMinus1 = (int)reader.ReadUe();
        var picHeightInMapUnitsMinus1 = (int)reader.ReadUe();

        var frameMbsOnly = reader.ReadFlag();
        if (!frameMbsOnly)
        {
            reader.SkipBits(1); // mb_adaptive_frame_field_flag
        }

        reader.SkipBits(1); // direct_8x8_inference_flag

        var cropLeft = 0;
        var cropRight = 0;
        var cropTop = 0;
        var cropBottom = 0;

        if (reader.ReadFlag()) // frame_cropping_flag
        {
            cropLeft = (int)reader.ReadUe();
            cropRight = (int)reader.ReadUe();
            cropTop = (int)reader.ReadUe();
            cropBottom = (int)reader.ReadUe();
        }

        var hasVui = reader.ReadFlag();

        var width = (picWidthInMbsMinus1 + 1) * 16;
        var height = (2 - (frameMbsOnly ? 1 : 0)) * (picHeightInMapUnitsMinus1 + 1) * 16;

        var (cropUnitX, cropUnitY) = GetCropUnits(chromaFormatIdc, separateColourPlane, frameMbsOnly);
        width -= (cropLeft + cropRight) * cropUnitX;
        height -= (cropTop + cropBottom) * cropUnitY;

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"SPS 解析出的分辨率非法: {width}x{height}");
        }

        return new H264SpsInfo(
            spsId,
            profileIdc,
            levelIdc,
            chromaFormatIdc,
            width,
            height,
            maxNumRefFrames,
            frameMbsOnly,
            constraintFlags,
            hasVui);
    }

    /// <summary>去除 emulation prevention（00 00 03 → 00 00）。</summary>
    public static byte[] ToRbsp(ReadOnlySpan<byte> data)
    {
        var result = new byte[data.Length];
        var length = 0;
        var zeros = 0;

        foreach (var value in data)
        {
            if (zeros >= 2 && value == 3)
            {
                zeros = 0;
                continue;
            }

            result[length++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }

        return length == result.Length ? result : result[..length];
    }

    private static void SkipScalingList(ref BitReader reader, int size)
    {
        var lastScale = 8;
        var nextScale = 8;

        for (var index = 0; index < size; index++)
        {
            if (nextScale != 0)
            {
                var delta = reader.ReadSe();
                nextScale = (lastScale + delta + 256) % 256;
            }

            lastScale = nextScale == 0 ? lastScale : nextScale;
        }
    }

    private static (int CropUnitX, int CropUnitY) GetCropUnits(int chromaFormatIdc, bool separateColourPlane, bool frameMbsOnly)
    {
        var frameUnit = 2 - (frameMbsOnly ? 1 : 0);

        if (separateColourPlane || chromaFormatIdc == 0)
        {
            return (1, frameUnit);
        }

        return chromaFormatIdc switch
        {
            1 => (2, 2 * frameUnit),
            2 => (2, frameUnit),
            _ => (1, frameUnit),
        };
    }
}
