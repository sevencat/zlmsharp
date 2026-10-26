using System.Buffers;
using System.Buffers.Binary;

namespace ZlmSharp.Codecs;

/// <summary>SPS/PPS + 长度前缀宽度，等价于 AVCDecoderConfigurationRecord 的关键信息。</summary>
public sealed class H264Config
{
    public required byte[] Sps { get; init; }

    public required byte[] Pps { get; init; }

    /// <summary>AVCC 长度前缀字节数（1~4，通常为 4）。</summary>
    public int LengthSize { get; init; } = 4;

    public int ProfileIdc => Sps.Length > 1 ? Sps[1] : 0;

    public int LevelIdc => Sps.Length > 3 ? Sps[3] : 0;

    /// <summary>生成 FLV/MP4 用的 AVCDecoderConfigurationRecord。</summary>
    public byte[] BuildAvcConfigRecord()
    {
        var length = 5 + 1 + 2 + Sps.Length + 1 + 2 + Pps.Length;
        var record = new byte[length];
        var offset = 0;

        record[offset++] = 1; // configurationVersion
        record[offset++] = (byte)ProfileIdc;
        record[offset++] = Sps.Length > 2 ? Sps[2] : (byte)0; // profile_compatibility
        record[offset++] = (byte)LevelIdc;
        record[offset++] = (byte)(0xFC | ((LengthSize - 1) & 0x03)); // reserved + lengthSizeMinusOne
        record[offset++] = 0xE1; // reserved + numOfSequenceParameterSets(1)

        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(offset), (ushort)Sps.Length);
        offset += 2;
        Sps.CopyTo(record, offset);
        offset += Sps.Length;

        record[offset++] = 1; // numOfPictureParameterSets
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(offset), (ushort)Pps.Length);
        offset += 2;
        Pps.CopyTo(record, offset);

        return record;
    }

    /// <summary>Annex-B 形式的参数集：00 00 00 01 SPS 00 00 00 01 PPS。</summary>
    public byte[] BuildAnnexBParameterSets()
    {
        var result = new byte[4 + Sps.Length + 4 + Pps.Length];
        var offset = 0;
        result[offset + 3] = 1;
        offset += 4;
        Sps.CopyTo(result, offset);
        offset += Sps.Length;
        result[offset + 3] = 1;
        offset += 4;
        Pps.CopyTo(result, offset);
        return result;
    }
}

/// <summary>AVCC（长度前缀）与 Annex-B（起始码）互转。</summary>
public static class H264Avcc
{
    public static bool TryParseConfigRecord(ReadOnlySpan<byte> record, out H264Config? config, out string error)
    {
        config = null;
        error = string.Empty;

        if (record.Length < 7 || record[0] != 1)
        {
            error = "AVCDecoderConfigurationRecord 非法";
            return false;
        }

        var lengthSize = (record[4] & 0x03) + 1;
        var offset = 5;
        var spsCount = record[offset++] & 0x1F;

        byte[]? sps = null;
        for (var i = 0; i < spsCount; i++)
        {
            if (offset + 2 > record.Length)
            {
                error = "SPS 长度字段越界";
                return false;
            }

            var size = BinaryPrimitives.ReadUInt16BigEndian(record[offset..]);
            offset += 2;
            if (offset + size > record.Length)
            {
                error = "SPS 数据越界";
                return false;
            }

            sps ??= record.Slice(offset, size).ToArray();
            offset += size;
        }

        if (sps is null || offset >= record.Length)
        {
            error = "缺少 SPS";
            return false;
        }

        var ppsCount = record[offset++];
        byte[]? pps = null;
        for (var i = 0; i < ppsCount; i++)
        {
            if (offset + 2 > record.Length)
            {
                error = "PPS 长度字段越界";
                return false;
            }

            var size = BinaryPrimitives.ReadUInt16BigEndian(record[offset..]);
            offset += 2;
            if (offset + size > record.Length)
            {
                error = "PPS 数据越界";
                return false;
            }

            pps ??= record.Slice(offset, size).ToArray();
            offset += size;
        }

        if (pps is null)
        {
            error = "缺少 PPS";
            return false;
        }

        config = new H264Config { Sps = sps, Pps = pps, LengthSize = lengthSize };
        return true;
    }

    /// <summary>把 AVCC 样本（[len][nalu]...）转成 Annex-B 写入 <paramref name="writer"/>，返回 NALU 数量。</summary>
    public static int ToAnnexB(ReadOnlySpan<byte> avcc, int lengthSize, IBufferWriter<byte> writer)
    {
        if (lengthSize is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthSize));
        }

        var offset = 0;
        var count = 0;

        while (offset < avcc.Length)
        {
            if (offset + lengthSize > avcc.Length)
            {
                throw new InvalidDataException("AVCC 长度字段不完整");
            }

            var nalLength = ReadLength(avcc.Slice(offset, lengthSize));
            offset += lengthSize;

            if (nalLength <= 0 || offset + nalLength > avcc.Length)
            {
                throw new InvalidDataException($"AVCC NALU 长度非法: {nalLength}");
            }

            var destination = writer.GetSpan(4 + nalLength);
            destination[0] = 0;
            destination[1] = 0;
            destination[2] = 0;
            destination[3] = 1;
            avcc.Slice(offset, nalLength).CopyTo(destination[4..]);
            writer.Advance(4 + nalLength);

            offset += nalLength;
            count++;
        }

        return count;
    }

    /// <summary>把一组 NALU 写成 AVCC 样本（大端长度前缀），返回写入的 NALU 数量。</summary>
    public static int ToAvcc(IEnumerable<ReadOnlyMemory<byte>> nalUnits, int lengthSize, IBufferWriter<byte> writer)
    {
        if (lengthSize is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthSize));
        }

        var count = 0;
        foreach (var nalUnit in nalUnits)
        {
            var span = nalUnit.Span;
            if (span.IsEmpty)
            {
                continue;
            }

            var destination = writer.GetSpan(lengthSize + span.Length);
            WriteLength(destination[..lengthSize], span.Length);
            span.CopyTo(destination[lengthSize..]);
            writer.Advance(lengthSize + span.Length);
            count++;
        }

        return count;
    }

    /// <summary>AVCC 样本里是否有关键帧（IDR）。</summary>
    public static bool ContainsIdr(ReadOnlySpan<byte> avcc, int lengthSize)
    {
        var offset = 0;
        while (offset + lengthSize <= avcc.Length)
        {
            var nalLength = ReadLength(avcc.Slice(offset, lengthSize));
            offset += lengthSize;

            if (nalLength <= 0 || offset + nalLength > avcc.Length)
            {
                return false;
            }

            if ((avcc[offset] & 0x1F) == (byte)H264NalUnitType.IdrSlice)
            {
                return true;
            }

            offset += nalLength;
        }

        return false;
    }

    public static int ReadLength(ReadOnlySpan<byte> prefix) => prefix.Length switch
    {
        1 => prefix[0],
        2 => BinaryPrimitives.ReadUInt16BigEndian(prefix),
        3 => (prefix[0] << 16) | (prefix[1] << 8) | prefix[2],
        4 => (int)BinaryPrimitives.ReadUInt32BigEndian(prefix),
        _ => throw new ArgumentOutOfRangeException(nameof(prefix)),
    };

    private static void WriteLength(Span<byte> destination, int length)
    {
        switch (destination.Length)
        {
            case 1:
                destination[0] = (byte)length;
                break;
            case 2:
                BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)length);
                break;
            case 3:
                destination[0] = (byte)(length >> 16);
                destination[1] = (byte)(length >> 8);
                destination[2] = (byte)length;
                break;
            case 4:
                BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(destination));
        }
    }
}
