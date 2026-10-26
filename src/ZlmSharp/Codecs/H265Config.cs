using System.Buffers.Binary;

namespace ZlmSharp.Codecs;

/// <summary>VPS/SPS/PPS + 长度前缀宽度，等价于 HEVCDecoderConfigurationRecord 的关键信息。</summary>
public sealed class H265Config
{
    public required byte[] Vps { get; init; }

    public required byte[] Sps { get; init; }

    public required byte[] Pps { get; init; }

    /// <summary>AVCC 风格长度前缀字节数（1~4，通常为 4）。</summary>
    public int LengthSize { get; init; } = 4;

    /// <summary>从 SPS 解析出的 profile（失败时退回裸字节）。</summary>
    public int ProfileIdc =>
        H265SpsParser.TryParse(Sps, out var info, out _) && info is not null
            ? info.ProfileIdc
            : Sps.Length > 3 ? Sps[3] & 0x1F : 1;

    /// <summary>生成 FLV / MP4 用的 HEVCDecoderConfigurationRecord（hvcC）。</summary>
    public byte[] BuildHvcConfigRecord()
    {
        var hasInfo = H265SpsParser.TryParse(Sps, out var info, out _) && info is not null;
        var profileSpace = hasInfo ? info!.ProfileSpace : 0;
        var tier = hasInfo ? info!.TierFlag : 0;
        var profileIdc = hasInfo ? info!.ProfileIdc : ProfileIdc;
        var compatFlags = hasInfo ? info!.ProfileCompatibilityFlags : 0u;
        var constraintFlags = hasInfo ? info!.ConstraintFlags : 0ul;
        var levelIdc = hasInfo ? info!.LevelIdc : 0;
        var chromaFormat = hasInfo ? info!.ChromaFormatIdc : 1;
        var bitDepthLumaMinus8 = (hasInfo ? info!.BitDepthLuma : 8) - 8;
        var bitDepthChromaMinus8 = (hasInfo ? info!.BitDepthChroma : 8) - 8;
        var numTemporalLayers = hasInfo ? Math.Clamp(info!.MaxSubLayersMinus1 + 1, 1, 7) : 1;
        var temporalIdNested = hasInfo && info!.TemporalIdNested;

        var length = 23 + 3 * 5 + Vps.Length + Sps.Length + Pps.Length;
        var record = new byte[length];
        var offset = 0;

        record[offset++] = 1; // configurationVersion
        record[offset++] = (byte)((profileSpace << 6) | (tier << 5) | (profileIdc & 0x1F));
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(offset), compatFlags);
        offset += 4;
        // 48 位 general_constraint_indicator_flags
        for (var i = 5; i >= 0; i--)
        {
            record[offset++] = (byte)((constraintFlags >> (i * 8)) & 0xFF);
        }

        record[offset++] = (byte)levelIdc;
        record[offset++] = 0xF0; // min_spatial_segmentation_idc（reserved + 0）
        record[offset++] = 0x00;
        record[offset++] = 0xFC; // parallelismType
        record[offset++] = (byte)(0xFC | (chromaFormat & 0x03));
        record[offset++] = (byte)(0xF8 | (bitDepthLumaMinus8 & 0x07));
        record[offset++] = (byte)(0xF8 | (bitDepthChromaMinus8 & 0x07));
        record[offset++] = 0x00; // avgFrameRate
        record[offset++] = 0x00;
        record[offset++] = (byte)((numTemporalLayers << 3) | (temporalIdNested ? 0x04 : 0) | ((LengthSize - 1) & 0x03));
        record[offset++] = 3; // numOfArrays

        WriteArray(record, ref offset, H265NalUnitType.Vps, Vps);
        WriteArray(record, ref offset, H265NalUnitType.Sps, Sps);
        WriteArray(record, ref offset, H265NalUnitType.Pps, Pps);

        return record;
    }

    /// <summary>Annex-B 形式的参数集：VPS / SPS / PPS 各带 4 字节起始码。</summary>
    public byte[] BuildAnnexBParameterSets()
    {
        var result = new byte[(4 + Vps.Length) + (4 + Sps.Length) + (4 + Pps.Length)];
        var offset = 0;

        foreach (var nal in new[] { Vps, Sps, Pps })
        {
            result[offset + 3] = 1;
            offset += 4;
            nal.CopyTo(result, offset);
            offset += nal.Length;
        }

        return result;
    }

    /// <summary>解析 FLV/MP4 里的 HEVCDecoderConfigurationRecord。</summary>
    public static bool TryParseConfigRecord(ReadOnlySpan<byte> record, out H265Config? config, out string error)
    {
        config = null;
        error = string.Empty;

        if (record.Length < 23 || record[0] != 1)
        {
            error = "HEVCDecoderConfigurationRecord 非法";
            return false;
        }

        var lengthSize = (record[21] & 0x03) + 1;
        var arrayCount = record[22];
        var offset = 23;

        byte[]? vps = null;
        byte[]? sps = null;
        byte[]? pps = null;

        for (var array = 0; array < arrayCount; array++)
        {
            if (offset + 3 > record.Length)
            {
                error = "hvcC array 头越界";
                return false;
            }

            var nalType = record[offset] & 0x3F;
            var count = BinaryPrimitives.ReadUInt16BigEndian(record[(offset + 1)..]);
            offset += 3;

            for (var i = 0; i < count; i++)
            {
                if (offset + 2 > record.Length)
                {
                    error = "hvcC NALU 长度字段越界";
                    return false;
                }

                var size = BinaryPrimitives.ReadUInt16BigEndian(record[offset..]);
                offset += 2;
                if (size <= 0 || offset + size > record.Length)
                {
                    error = "hvcC NALU 数据越界";
                    return false;
                }

                var data = record.Slice(offset, size).ToArray();
                offset += size;

                switch ((H265NalUnitType)nalType)
                {
                    case H265NalUnitType.Vps:
                        vps ??= data;
                        break;
                    case H265NalUnitType.Sps:
                        sps ??= data;
                        break;
                    case H265NalUnitType.Pps:
                        pps ??= data;
                        break;
                }
            }
        }

        if (vps is null || sps is null || pps is null)
        {
            error = "hvcC 缺少 VPS/SPS/PPS";
            return false;
        }

        config = new H265Config { Vps = vps, Sps = sps, Pps = pps, LengthSize = lengthSize };
        return true;
    }

    private static void WriteArray(byte[] record, ref int offset, H265NalUnitType type, byte[] nal)
    {
        record[offset++] = (byte)(0x80 | ((int)type & 0x3F)); // array_completeness + NAL type
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(offset), 1);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(offset), (ushort)nal.Length);
        offset += 2;
        nal.CopyTo(record, offset);
        offset += nal.Length;
    }
}
