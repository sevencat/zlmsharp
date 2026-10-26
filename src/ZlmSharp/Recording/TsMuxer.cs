using System.Buffers;
using System.Buffers.Binary;

namespace ZlmSharp.Recording;

/// <summary>
/// 最小 MPEG-TS 封装（HLS 切片用）：PAT/PMT + H.264（PES 0xE0）+ AAC（ADTS，PES 0xC0），
/// 188 字节定长包，PCR 挂在视频 PID 上。只支持本项目现有场景（H.264 + 可选 AAC）。
/// </summary>
internal sealed class TsMuxer : IDisposable
{
    private const int PacketSize = 188;
    private const int PidPmt = 0x1000;
    private const int PidVideo = 0x100;
    private const int PidAudio = 0x101;
    private const int PsiIntervalMs = 1000;

    private static readonly int[] SamplingRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000,
        24000, 22050, 16000, 12000, 11025, 8000, 7350,
    ];

    private readonly Stream _stream;
    private readonly byte[] _packet = new byte[PacketSize];
    private readonly byte[] _continuity = new byte[0x2000];

    private readonly bool _hasAudio;
    private readonly int _sampleRateIndex;
    private readonly int _channels;

    private long _lastPsiMs = long.MinValue;
    private bool _disposed;

    public TsMuxer(Stream stream, byte[]? audioSpecificConfig, int sampleRate, int channels)
    {
        _stream = stream;

        if (audioSpecificConfig is { Length: >= 2 })
        {
            ParseAudioConfig(audioSpecificConfig, out var parsedRate, out var parsedChannels);
            sampleRate = sampleRate > 0 ? sampleRate : parsedRate;
            channels = channels > 0 ? channels : parsedChannels;
        }

        _sampleRateIndex = IndexOfSampleRate(sampleRate);
        _channels = channels is > 0 and <= 6 ? channels : 2;
        _hasAudio = audioSpecificConfig is { Length: >= 2 } && _sampleRateIndex >= 0;
    }

    public long BytesWritten { get; private set; }

    public void WriteVideo(ReadOnlySpan<byte> annexB, long dtsMs, long ptsMs, bool keyFrame)
    {
        EnsurePsi(dtsMs, force: keyFrame);

        var normalized = NormalizeAnnexB(annexB);
        var buffer = ArrayPool<byte>.Shared.Rent(19 + normalized.Length);
        try
        {
            var length = BuildVideoPes(buffer, normalized, dtsMs, ptsMs);
            WriteTsPacket(PidVideo, buffer.AsSpan(0, length), payloadUnitStart: true, writePcr: true, pcr90k: dtsMs * 90);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void WriteAudio(ReadOnlySpan<byte> aac, long dtsMs)
    {
        if (!_hasAudio || aac.Length == 0)
        {
            return;
        }

        // ADTS 头 7 字节 + AAC 原始帧
        var buffer = ArrayPool<byte>.Shared.Rent(14 + 7 + aac.Length);
        try
        {
            var payloadLength = WriteAdts(buffer.AsSpan(14), aac, _sampleRateIndex, _channels);
            aac.CopyTo(buffer.AsSpan(14 + 7));

            var length = BuildPes(
                buffer,
                streamId: 0xC0,
                payloadLength,
                dtsMs,
                dtsMs,
                withDts: false);

            WriteTsPacket(PidAudio, buffer.AsSpan(0, length), payloadUnitStart: true, writePcr: false, pcr90k: 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Flush()
    {
        _stream.Flush();
        BytesWritten = _stream.Length;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Flush();
        _stream.Dispose();
    }

    private void EnsurePsi(long dtsMs, bool force)
    {
        if (!force && dtsMs - _lastPsiMs < PsiIntervalMs)
        {
            return;
        }

        _lastPsiMs = dtsMs;
        WriteTsPacket(0, BuildPat(), payloadUnitStart: true, writePcr: false, pcr90k: 0);
        WriteTsPacket(PidPmt, BuildPmt(_hasAudio), payloadUnitStart: true, writePcr: false, pcr90k: 0);
    }

    private int BuildVideoPes(byte[] buffer, byte[] annexB, long dtsMs, long ptsMs)
    {
        var length = BuildPes(buffer, 0xE0, annexB.Length, dtsMs, ptsMs, withDts: true);
        annexB.CopyTo(buffer, length - annexB.Length);
        return length;
    }

    /// <summary>构造 PES 头（可选写入 payload 占位），返回总长度。</summary>
    private int BuildPes(
        byte[] buffer,
        byte streamId,
        int payloadLength,
        long dtsMs,
        long ptsMs,
        bool withDts)
    {
        var headerLength = 9 + (withDts ? 10 : 5);
        var total = headerLength + payloadLength;

        buffer[0] = 0x00;
        buffer[1] = 0x00;
        buffer[2] = 0x01;
        buffer[3] = streamId;

        // 视频（0xE0）允许 PES_packet_length = 0（不限长）；音频必须写实际长度
        var pesLength = payloadLength + 3 + (withDts ? 10 : 5);
        if (streamId == 0xE0 || pesLength > 0xFFFF)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4), 0);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4), (ushort)pesLength);
        }

        buffer[6] = 0x84; // marker bits + data_alignment_indicator
        buffer[7] = (byte)(withDts ? 0xC0 : 0x80);
        buffer[8] = (byte)(withDts ? 10 : 5);

        var pts90k = ptsMs * 90;
        var dts90k = dtsMs * 90;

        if (withDts)
        {
            WriteTimestamp(buffer.AsSpan(9), prefix: 0x3, pts90k);
            WriteTimestamp(buffer.AsSpan(14), prefix: 0x1, dts90k);
        }
        else
        {
            WriteTimestamp(buffer.AsSpan(9), prefix: 0x2, pts90k);
        }

        return total;
    }

    private static void WriteTimestamp(Span<byte> buffer, byte prefix, long value)
    {
        buffer[0] = (byte)((prefix << 4) | ((int)((value >> 30) & 0x07) << 1) | 1);
        buffer[1] = (byte)((value >> 22) & 0xFF);
        buffer[2] = (byte)((((value >> 15) & 0x7F) << 1) | 1);
        buffer[3] = (byte)((value >> 7) & 0xFF);
        buffer[4] = (byte)(((value & 0x7F) << 1) | 1);
    }

    private void WriteTsPacket(int pid, ReadOnlySpan<byte> payload, bool payloadUnitStart, bool writePcr, long pcr90k)
    {
        var offset = 0;
        var first = true;

        while (offset < payload.Length)
        {
            var needPcr = writePcr && first;
            var capacity = 184 - (needPcr ? 8 : 0);

            var count = Math.Min(capacity, payload.Length - offset);
            var adaptationTotal = 184 - count;

            _packet.AsSpan().Clear();
            _packet[0] = 0x47;
            var pusi = payloadUnitStart && first ? 0x40 : 0x00;
            _packet[1] = (byte)(pusi | ((pid >> 8) & 0x1F));
            _packet[2] = (byte)(pid & 0xFF);

            var counter = _continuity[pid];
            _continuity[pid] = (byte)((counter + 1) & 0x0F);

            var hasAdaptation = adaptationTotal > 0;
            _packet[3] = (byte)((hasAdaptation ? 0x30 : 0x10) | counter);

            var position = 4;
            if (adaptationTotal == 1)
            {
                _packet[position++] = 0x00;
            }
            else if (adaptationTotal >= 2)
            {
                _packet[position++] = (byte)(adaptationTotal - 1);
                _packet[position++] = (byte)(needPcr ? 0x10 : 0x00);

                if (needPcr)
                {
                    WritePcr(_packet.AsSpan(position), pcr90k);
                    position += 6;
                }

                while (position < 4 + adaptationTotal)
                {
                    _packet[position++] = 0xFF;
                }
            }

            payload.Slice(offset, count).CopyTo(_packet.AsSpan(position));
            _stream.Write(_packet, 0, PacketSize);

            offset += count;
            first = false;
        }
    }

    private static void WritePcr(Span<byte> buffer, long base90k)
    {
        buffer[0] = (byte)((base90k >> 25) & 0xFF);
        buffer[1] = (byte)((base90k >> 17) & 0xFF);
        buffer[2] = (byte)((base90k >> 9) & 0xFF);
        buffer[3] = (byte)((base90k >> 1) & 0xFF);
        buffer[4] = (byte)(((base90k & 0x01) << 7) | 0x7E);
        buffer[5] = 0x00;
    }

    private static byte[] BuildPat()
    {
        var section = new byte[16];
        section[0] = 0x00; // table_id PAT
        section[1] = 0xB0;
        section[2] = 0x0D; // section_length = 13
        BinaryPrimitives.WriteUInt16BigEndian(section.AsSpan(3), 0x0001); // transport_stream_id
        section[5] = 0xC1; // version 0, current_next 1
        section[6] = 0x00;
        section[7] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(section.AsSpan(8), 0x0001); // program_number
        section[10] = (byte)(0xE0 | (PidPmt >> 8));
        section[11] = (byte)(PidPmt & 0xFF);
        WriteCrc32(section);

        var payload = new byte[1 + section.Length];
        payload[0] = 0x00; // pointer_field
        section.CopyTo(payload, 1);
        return payload;
    }

    private static byte[] BuildPmt(bool hasAudio)
    {
        var streams = hasAudio ? 2 : 1;
        var sectionLength = 9 + (streams * 5) + 4;
        var section = new byte[3 + sectionLength];

        section[0] = 0x02;
        section[1] = 0xB0;
        section[2] = (byte)(sectionLength & 0xFF);
        BinaryPrimitives.WriteUInt16BigEndian(section.AsSpan(3), 0x0001); // program_number
        section[5] = 0xC1;
        section[6] = 0x00;
        section[7] = 0x00;
        section[8] = (byte)(0xE0 | (PidVideo >> 8)); // PCR_PID
        section[9] = (byte)(PidVideo & 0xFF);
        section[10] = 0xF0; // program_info_length = 0
        section[11] = 0x00;

        var position = 12;
        position = WriteStream(section, position, 0x1B, PidVideo);
        if (hasAudio)
        {
            WriteStream(section, position, 0x0F, PidAudio);
        }

        WriteCrc32(section);
        var payload = new byte[1 + section.Length];
        payload[0] = 0x00;
        section.CopyTo(payload, 1);
        return payload;
    }

    private static int WriteStream(byte[] section, int position, byte streamType, int pid)
    {
        section[position++] = streamType;
        section[position++] = (byte)(0xE0 | (pid >> 8));
        section[position++] = (byte)(pid & 0xFF);
        section[position++] = 0xF0;
        section[position++] = 0x00;
        return position;
    }

    /// <summary>MPEG-2 CRC32（多项式 0x04C11DB7，不取反）。</summary>
    private static void WriteCrc32(Span<byte> section)
    {
        var crc = 0xFFFFFFFFu;
        for (var index = 0; index < section.Length - 4; index++)
        {
            crc ^= (uint)section[index] << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(section[^4..], crc);
    }

    /// <summary>把任意起始码（3/4 字节）统一成 4 字节；无起始码则整体当作单个 NAL。</summary>
    private static byte[] NormalizeAnnexB(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length + 8);
        var index = 0;
        var found = false;

        while (index + 3 <= data.Length)
        {
            if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1)
            {
                if (!found)
                {
                    output.AddRange([0, 0, 0, 1]);
                    found = true;
                    index += 3;
                }
                else
                {
                    // 覆盖前一个 NAL 的尾部 0？直接换成 4 字节起始码
                    output.AddRange([0, 0, 0, 1]);
                    index += 3;
                }
            }
            else if (index + 4 <= data.Length &&
                     data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 0 && data[index + 3] == 1)
            {
                output.AddRange([0, 0, 0, 1]);
                found = true;
                index += 4;
            }
            else
            {
                output.Add(data[index]);
                index++;
            }
        }

        while (index < data.Length)
        {
            output.Add(data[index++]);
        }

        if (!found)
        {
            output.InsertRange(0, new byte[] { 0, 0, 0, 1 });
        }

        return [.. output];
    }

    private static int WriteAdts(Span<byte> header, ReadOnlySpan<byte> aac, int sampleRateIndex, int channels)
    {
        var frameLength = aac.Length + 7;
        header[0] = 0xFF;
        header[1] = 0xF1; // MPEG-4, layer 0, no CRC
        header[2] = (byte)((1 << 6) | ((sampleRateIndex & 0x0F) << 2) | ((channels >> 2) & 0x01));
        header[3] = (byte)(((channels & 0x03) << 6) | ((frameLength >> 11) & 0x03));
        header[4] = (byte)((frameLength >> 3) & 0xFF);
        header[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
        header[6] = 0xFC;
        return 7;
    }

    private static int IndexOfSampleRate(int sampleRate)
    {
        for (var index = 0; index < SamplingRates.Length; index++)
        {
            if (SamplingRates[index] == sampleRate)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool ParseAudioConfig(ReadOnlySpan<byte> asc, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 2;

        if (asc.Length < 2)
        {
            return false;
        }

        var frequencyIndex = (asc[0] >> 2) & 0x0F;
        if (frequencyIndex == 0x0F)
        {
            if (asc.Length < 5)
            {
                return false;
            }

            sampleRate = ((asc[1] & 0x7F) << 17) | (asc[2] << 9) | (asc[3] << 1) | ((asc[4] >> 7) & 0x01);
        }
        else if (frequencyIndex < SamplingRates.Length)
        {
            sampleRate = SamplingRates[frequencyIndex];
        }

        var channelConfig = (asc[1] >> 3) & 0x0F;
        channels = channelConfig switch
        {
            0 => 0,
            7 => 8,
            _ => channelConfig,
        };

        if (channels <= 0)
        {
            channels = 1;
        }

        return sampleRate > 0;
    }
}
