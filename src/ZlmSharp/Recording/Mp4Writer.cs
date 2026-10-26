using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using ZlmSharp.Codecs;

namespace ZlmSharp.Recording;

/// <summary>
/// 最小 MP4（ISO BMFF）封装：H.264（avc1/avcC）或 H.265（hvc1/hvcC）+ AAC（raw），
/// 普通 MP4、moov 写在文件末尾。只服务录像（非流式播放），假设 DTS 单调递增；B 帧通过 ctts 表达。
/// 不支持：分片 MP4、edit list、多参数集切换。
/// </summary>
internal sealed class Mp4Writer : IDisposable
{
    private const int VideoTimescale = 90000;
    private const int AacFrameSamples = 1024;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly VideoCodecConfig _videoConfig;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[]? _audioConfig;
    private readonly int _audioSampleRate;
    private readonly int _audioChannels;
    private readonly List<Sample> _videoSamples = [];
    private readonly List<Sample> _audioSamples = [];

    private readonly long _mdatDataStart;
    private readonly long _mdatSizeOffset;

    private bool _completed;

    public Mp4Writer(
        Stream stream,
        H264Config videoConfig,
        int width,
        int height,
        ReadOnlyMemory<byte> audioConfig = default,
        int audioSampleRate = 0,
        int audioChannels = 0,
        bool ownsStream = true)
        : this(
            stream,
            VideoCodecConfig.FromH264(videoConfig),
            width,
            height,
            audioConfig,
            audioSampleRate,
            audioChannels,
            ownsStream)
    {
    }

    public Mp4Writer(
        Stream stream,
        VideoCodecConfig videoConfig,
        int width,
        int height,
        ReadOnlyMemory<byte> audioConfig = default,
        int audioSampleRate = 0,
        int audioChannels = 0,
        bool ownsStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(videoConfig);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);

        _stream = stream;
        _ownsStream = ownsStream;
        _videoConfig = videoConfig;
        _width = width;
        _height = height;
        _audioConfig = audioConfig.Length > 0 ? audioConfig.ToArray() : null;
        _audioSampleRate = audioSampleRate;
        _audioChannels = audioChannels;

        WriteFtyp();
        _mdatSizeOffset = stream.Position + 8;
        WriteUInt32(stream, 1);           // size=1 → 使用 64 位 largesize
        WriteAscii(stream, "mdat");
        WriteUInt64(stream, 0);           // largesize，收尾时回填
        _mdatDataStart = stream.Position;
    }

    public string Path => (_stream as FileStream)?.Name ?? string.Empty;

    public int VideoSampleCount => _videoSamples.Count;

    public int AudioSampleCount => _audioSamples.Count;

    public long DurationMs { get; private set; }

    /// <summary>写入一帧 H.264（Annex-B，含一个访问单元）。</summary>
    public void WriteVideo(ReadOnlySpan<byte> annexB, long dts, long pts, bool keyFrame)
    {
        if (_completed || annexB.IsEmpty)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(annexB.Length + 256);
        int length;
        try
        {
            length = ConvertAnnexBToAvcc(annexB, buffer);
            if (length == 0)
            {
                return;
            }

            var offset = _stream.Position;
            _stream.Write(buffer, 0, length);

            var composition = (int)Math.Clamp((pts - dts) * 90, int.MinValue, int.MaxValue);
            _videoSamples.Add(new Sample
            {
                Offset = offset,
                Size = length,
                Dts = dts,
                CompositionOffset = composition,
                Key = keyFrame,
            });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>写入一帧 AAC 原始访问单元（不含 ADTS）。</summary>
    public void WriteAudio(ReadOnlySpan<byte> rawAac, long dts)
    {
        if (_completed || rawAac.IsEmpty || !HasAudio)
        {
            return;
        }

        var offset = _stream.Position;
        _stream.Write(rawAac);
        _audioSamples.Add(new Sample
        {
            Offset = offset,
            Size = rawAac.Length,
            Dts = dts,
            Key = true,
        });
    }

    public bool HasAudio => _audioConfig is { Length: > 0 } && _audioSampleRate > 0;

    /// <summary>收尾：回填 mdat 长度并写 moov（必须调用，否则文件不可播）。</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;

        var videoDurations = BuildVideoDurations();
        var audioDurationTicks = _audioSamples.Count * AacFrameSamples;
        var videoDurationTicks = 0L;
        foreach (var duration in videoDurations)
        {
            videoDurationTicks += duration;
        }

        var videoDurationMs = videoDurationTicks * 1000 / VideoTimescale;
        var audioDurationMs = HasAudio ? (long)_audioSamples.Count * 1000 * AacFrameSamples / _audioSampleRate : 0;
        DurationMs = Math.Max(videoDurationMs, audioDurationMs);
        var movieDuration = (uint)Math.Clamp(DurationMs, 0, uint.MaxValue);

        using var moov = new MemoryStream(64 * 1024);
        WriteMoov(moov, movieDuration, videoDurations, videoDurationTicks, audioDurationTicks);

        _stream.Flush();
        var dataEnd = _stream.Position;
        _stream.Position = _mdatSizeOffset;
        WriteUInt64(_stream, (ulong)(dataEnd - _mdatDataStart + 16));
        _stream.Position = dataEnd;

        moov.Position = 0;
        moov.CopyTo(_stream);
        _stream.Flush();
    }

    public void Dispose()
    {
        try
        {
            Complete();
        }
        finally
        {
            if (_ownsStream)
            {
                _stream.Dispose();
            }
        }
    }

    private int[] BuildVideoDurations()
    {
        var durations = new int[_videoSamples.Count];
        for (var index = 0; index < _videoSamples.Count - 1; index++)
        {
            var delta = _videoSamples[index + 1].Dts - _videoSamples[index].Dts;
            durations[index] = (int)Math.Max(1, delta * VideoTimescale / 1000);
        }

        if (durations.Length > 0)
        {
            durations[^1] = durations.Length > 1 ? durations[^2] : VideoTimescale / 25;
        }

        return durations;
    }

    private void WriteMoov(Stream stream, uint movieDuration, int[] videoDurations, long videoDurationTicks, long audioDurationTicks)
    {
        WriteBox(stream, "moov", moov =>
        {
            WriteMvhd(moov, movieDuration);
            WriteVideoTrak(moov, movieDuration, videoDurations, videoDurationTicks);

            if (HasAudio && _audioSamples.Count > 0)
            {
                WriteAudioTrak(moov, movieDuration, audioDurationTicks);
            }
        });
    }

    private void WriteMvhd(Stream stream, uint movieDuration)
    {
        WriteBox(stream, "mvhd", mvhd =>
        {
            WriteFullBoxHeader(mvhd, 0, 0);
            WriteUInt32(mvhd, 0);              // creation_time
            WriteUInt32(mvhd, 0);              // modification_time
            WriteUInt32(mvhd, 1000);           // timescale
            WriteUInt32(mvhd, movieDuration);  // duration（ms）
            WriteUInt32(mvhd, 0x00010000);     // rate
            WriteUInt16(mvhd, 0x0100);         // volume
            WriteUInt16(mvhd, 0);              // reserved
            WriteUInt32(mvhd, 0);
            WriteUInt32(mvhd, 0);
            WriteMatrix(mvhd);
            for (var index = 0; index < 6; index++)
            {
                WriteUInt32(mvhd, 0);          // pre_defined
            }

            WriteUInt32(mvhd, HasAudio ? 3u : 2u); // next_track_ID
        });
    }

    private void WriteVideoTrak(Stream stream, uint movieDuration, int[] durations, long durationTicks)
    {
        var durationMs = (uint)Math.Min(movieDuration, (ulong)durationTicks * 1000 / VideoTimescale);

        WriteBox(stream, "trak", trak =>
        {
            WriteTkhd(trak, trackId: 1, durationMs, volume: 0, _width, _height);

            WriteBox(trak, "mdia", mdia =>
            {
                WriteMdhd(mdia, VideoTimescale, durationTicks);
                WriteHdlr(mdia, "vide", "VideoHandler");
                WriteBox(mdia, "minf", minf =>
                {
                    WriteBox(minf, "vmhd", vmhd =>
                    {
                        WriteFullBoxHeader(vmhd, 0, 1);
                        WriteUInt16(vmhd, 0); // graphicsmode
                        WriteUInt16(vmhd, 0);
                        WriteUInt16(vmhd, 0);
                        WriteUInt16(vmhd, 0); // opcolor
                    });

                    WriteDinf(minf);

                    WriteBox(minf, "stbl", stbl =>
                    {
                        WriteVideoStsd(stbl);
                        WriteStts(stbl, durations);

                        var syncSamples = new List<int>();
                        for (var index = 0; index < _videoSamples.Count; index++)
                        {
                            if (_videoSamples[index].Key)
                            {
                                syncSamples.Add(index + 1);
                            }
                        }

                        if (syncSamples.Count > 0 && syncSamples.Count < _videoSamples.Count)
                        {
                            WriteBox(stbl, "stss", stss =>
                            {
                                WriteFullBoxHeader(stss, 0, 0);
                                WriteUInt32(stss, (uint)syncSamples.Count);
                                foreach (var sample in syncSamples)
                                {
                                    WriteUInt32(stss, (uint)sample);
                                }
                            });
                        }

                        WriteCttsIfNeeded(stbl);
                        WriteStsc(stbl);
                        WriteStsz(stbl, _videoSamples);
                        WriteChunkOffsets(stbl, _videoSamples);
                    });
                });
            });
        });
    }

    private void WriteAudioTrak(Stream stream, uint movieDuration, long durationTicks)
    {
        var durationMs = (uint)Math.Min(movieDuration, (ulong)durationTicks * 1000 / (ulong)_audioSampleRate);

        WriteBox(stream, "trak", trak =>
        {
            WriteTkhd(trak, trackId: 2, durationMs, volume: 0x0100, width: 0, height: 0);

            WriteBox(trak, "mdia", mdia =>
            {
                WriteMdhd(mdia, _audioSampleRate, durationTicks);
                WriteHdlr(mdia, "soun", "SoundHandler");
                WriteBox(mdia, "minf", minf =>
                {
                    WriteBox(minf, "smhd", smhd =>
                    {
                        WriteFullBoxHeader(smhd, 0, 0);
                        WriteUInt16(smhd, 0); // balance
                        WriteUInt16(smhd, 0); // reserved
                    });

                    WriteDinf(minf);

                    WriteBox(minf, "stbl", stbl =>
                    {
                        WriteAudioStsd(stbl);
                        WriteStts(stbl, Enumerable.Repeat(AacFrameSamples, _audioSamples.Count).ToArray());
                        WriteStsc(stbl);
                        WriteStsz(stbl, _audioSamples);
                        WriteChunkOffsets(stbl, _audioSamples);
                    });
                });
            });
        });
    }

    private void WriteTkhd(Stream stream, int trackId, uint duration, int volume, int width, int height)
    {
        WriteBox(stream, "tkhd", tkhd =>
        {
            WriteFullBoxHeader(tkhd, 0, 0x000007); // enabled / in movie / in preview
            WriteUInt32(tkhd, 0);                  // creation_time
            WriteUInt32(tkhd, 0);                  // modification_time
            WriteUInt32(tkhd, (uint)trackId);
            WriteUInt32(tkhd, 0);                  // reserved
            WriteUInt32(tkhd, duration);
            WriteUInt32(tkhd, 0);                  // reserved
            WriteUInt32(tkhd, 0);
            WriteUInt16(tkhd, 0);                  // layer
            WriteUInt16(tkhd, 0);                  // alternate_group
            WriteUInt16(tkhd, (ushort)volume);
            WriteUInt16(tkhd, 0);                  // reserved
            WriteMatrix(tkhd);
            WriteUInt32(tkhd, (uint)width << 16);
            WriteUInt32(tkhd, (uint)height << 16);
        });
    }

    private static void WriteMdhd(Stream stream, int timescale, long duration)
    {
        WriteBox(stream, "mdhd", mdhd =>
        {
            WriteFullBoxHeader(mdhd, 0, 0);
            WriteUInt32(mdhd, 0);
            WriteUInt32(mdhd, 0);
            WriteUInt32(mdhd, (uint)timescale);
            WriteUInt32(mdhd, (uint)Math.Min(duration, uint.MaxValue));
            WriteUInt16(mdhd, 0x55C4); // 'und'
            WriteUInt16(mdhd, 0);
        });
    }

    private static void WriteHdlr(Stream stream, string handler, string name)
    {
        WriteBox(stream, "hdlr", hdlr =>
        {
            WriteFullBoxHeader(hdlr, 0, 0);
            WriteUInt32(hdlr, 0); // pre_defined
            WriteAscii(hdlr, handler);
            WriteUInt32(hdlr, 0);
            WriteUInt32(hdlr, 0);
            WriteUInt32(hdlr, 0);
            hdlr.Write(Encoding.ASCII.GetBytes(name));
            hdlr.WriteByte(0);
        });
    }

    private static void WriteDinf(Stream stream)
    {
        WriteBox(stream, "dinf", dinf =>
        {
            WriteBox(dinf, "dref", dref =>
            {
                WriteFullBoxHeader(dref, 0, 0);
                WriteUInt32(dref, 1);
                WriteBox(dref, "url ", url => WriteFullBoxHeader(url, 0, 1));
            });
        });
    }

    private void WriteVideoStsd(Stream stream)
    {
        WriteBox(stream, "stsd", stsd =>
        {
            WriteFullBoxHeader(stsd, 0, 0);
            WriteUInt32(stsd, 1);

            WriteBox(stsd, _videoConfig.IsHevc ? "hvc1" : "avc1", avc1 =>
            {
                for (var index = 0; index < 6; index++)
                {
                    avc1.WriteByte(0);
                }

                WriteUInt16(avc1, 1); // data_reference_index
                WriteUInt16(avc1, 0);
                WriteUInt16(avc1, 0);
                WriteUInt32(avc1, 0);
                WriteUInt32(avc1, 0);
                WriteUInt32(avc1, 0);
                WriteUInt16(avc1, (ushort)_width);
                WriteUInt16(avc1, (ushort)_height);
                WriteUInt32(avc1, 0x00480000); // horizresolution
                WriteUInt32(avc1, 0x00480000); // vertresolution
                WriteUInt32(avc1, 0);
                WriteUInt16(avc1, 1); // frame_count
                for (var index = 0; index < 32; index++)
                {
                    avc1.WriteByte(0); // compressorname
                }

                WriteUInt16(avc1, 0x0018); // depth
                WriteUInt16(avc1, 0xFFFF); // pre_defined

                var configBox = _videoConfig.IsHevc ? "hvcC" : "avcC";
                WriteBox(avc1, configBox, box => box.Write(_videoConfig.BuildConfigRecord()));
            });
        });
    }

    private void WriteAudioStsd(Stream stream)
    {
        WriteBox(stream, "stsd", stsd =>
        {
            WriteFullBoxHeader(stsd, 0, 0);
            WriteUInt32(stsd, 1);

            WriteBox(stsd, "mp4a", mp4a =>
            {
                for (var index = 0; index < 6; index++)
                {
                    mp4a.WriteByte(0);
                }

                WriteUInt16(mp4a, 1); // data_reference_index
                WriteUInt32(mp4a, 0);
                WriteUInt32(mp4a, 0);
                WriteUInt16(mp4a, (ushort)Math.Max(1, _audioChannels));
                WriteUInt16(mp4a, 16); // sample size
                WriteUInt16(mp4a, 0);
                WriteUInt16(mp4a, 0);
                WriteUInt32(mp4a, (uint)_audioSampleRate << 16);

                WriteEsds(mp4a, _audioConfig!);
            });
        });
    }

    private static void WriteEsds(Stream stream, byte[] audioConfig)
    {
        WriteBox(stream, "esds", esds =>
        {
            WriteFullBoxHeader(esds, 0, 0);

            using var decoder = new MemoryStream(32);
            decoder.WriteByte(0x40); // Audio ISO/IEC 14496-3
            decoder.WriteByte(0x15); // audio stream
            WriteUInt24(decoder, 0); // bufferSizeDB
            WriteUInt32(decoder, 0); // maxBitrate
            WriteUInt32(decoder, 0); // avgBitrate
            WriteDescriptor(decoder, 0x05, audioConfig);

            using var es = new MemoryStream(64);
            WriteUInt16(es, 1); // ES_ID
            es.WriteByte(0);    // flags
            WriteDescriptor(es, 0x04, decoder.ToArray());
            WriteDescriptor(es, 0x06, [0x02]); // SLConfigDescriptor

            WriteDescriptor(esds, 0x03, es.ToArray());
        });
    }

    private static void WriteDescriptor(Stream stream, byte tag, ReadOnlySpan<byte> payload)
    {
        stream.WriteByte(tag);
        // 4 字节 7-bit 长度（合法但不紧凑）
        stream.WriteByte((byte)(0x80 | ((payload.Length >> 21) & 0x7F)));
        stream.WriteByte((byte)(0x80 | ((payload.Length >> 14) & 0x7F)));
        stream.WriteByte((byte)(0x80 | ((payload.Length >> 7) & 0x7F)));
        stream.WriteByte((byte)(payload.Length & 0x7F));
        stream.Write(payload);
    }

    private static void WriteStts(Stream stream, int[] durations)
    {
        WriteBox(stream, "stts", stts =>
        {
            WriteFullBoxHeader(stts, 0, 0);

            var entries = new List<(int Count, int Duration)>();
            foreach (var duration in durations)
            {
                if (entries.Count > 0 && entries[^1].Duration == duration)
                {
                    var last = entries[^1];
                    entries[^1] = (last.Count + 1, last.Duration);
                }
                else
                {
                    entries.Add((1, duration));
                }
            }

            WriteUInt32(stts, (uint)entries.Count);
            foreach (var (count, duration) in entries)
            {
                WriteUInt32(stts, (uint)count);
                WriteUInt32(stts, (uint)duration);
            }
        });
    }

    private void WriteCttsIfNeeded(Stream stream)
    {
        var hasOffset = false;
        var hasNegative = false;
        foreach (var sample in _videoSamples)
        {
            if (sample.CompositionOffset != 0)
            {
                hasOffset = true;
            }

            if (sample.CompositionOffset < 0)
            {
                hasNegative = true;
            }
        }

        if (!hasOffset)
        {
            return;
        }

        WriteBox(stream, "ctts", ctts =>
        {
            WriteFullBoxHeader(ctts, hasNegative ? 1u : 0u, 0);

            var entries = new List<(int Count, int Offset)>();
            foreach (var sample in _videoSamples)
            {
                if (entries.Count > 0 && entries[^1].Offset == sample.CompositionOffset)
                {
                    var last = entries[^1];
                    entries[^1] = (last.Count + 1, last.Offset);
                }
                else
                {
                    entries.Add((1, sample.CompositionOffset));
                }
            }

            WriteUInt32(ctts, (uint)entries.Count);
            foreach (var (count, offset) in entries)
            {
                WriteUInt32(ctts, (uint)count);
                WriteUInt32(ctts, (uint)offset);
            }
        });
    }

    private static void WriteStsc(Stream stream)
    {
        WriteBox(stream, "stsc", stsc =>
        {
            WriteFullBoxHeader(stsc, 0, 0);
            WriteUInt32(stsc, 1);
            WriteUInt32(stsc, 1); // first_chunk
            WriteUInt32(stsc, 1); // samples_per_chunk
            WriteUInt32(stsc, 1); // sample_description_index
        });
    }

    private static void WriteStsz(Stream stream, List<Sample> samples)
    {
        WriteBox(stream, "stsz", stsz =>
        {
            WriteFullBoxHeader(stsz, 0, 0);
            WriteUInt32(stsz, 0); // sample_size（0 表示逐个给出）
            WriteUInt32(stsz, (uint)samples.Count);
            foreach (var sample in samples)
            {
                WriteUInt32(stsz, (uint)sample.Size);
            }
        });
    }

    private static void WriteChunkOffsets(Stream stream, List<Sample> samples)
    {
        var use64 = samples.Count > 0 && samples[^1].Offset > uint.MaxValue;
        WriteBox(stream, use64 ? "co64" : "stco", box =>
        {
            WriteFullBoxHeader(box, 0, 0);
            WriteUInt32(box, (uint)samples.Count);
            foreach (var sample in samples)
            {
                if (use64)
                {
                    WriteUInt64(box, (ulong)sample.Offset);
                }
                else
                {
                    WriteUInt32(box, (uint)sample.Offset);
                }
            }
        });
    }

    private void WriteFtyp()
    {
        WriteBox(_stream, "ftyp", ftyp =>
        {
            WriteAscii(ftyp, "isom");
            WriteUInt32(ftyp, 0x200);
            WriteAscii(ftyp, "isom");
            WriteAscii(ftyp, "iso2");
            WriteAscii(ftyp, _videoConfig.IsHevc ? "hvc1" : "avc1");
            WriteAscii(ftyp, "mp41");
        });
    }

    private static void WriteMatrix(Stream stream)
    {
        WriteUInt32(stream, 0x00010000);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0x00010000);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0x40000000);
    }

    private static void WriteFullBoxHeader(Stream stream, uint version, uint flags)
    {
        stream.WriteByte((byte)version);
        WriteUInt24(stream, (int)(flags & 0xFFFFFF));
    }

    private static void WriteBox(Stream stream, string type, Action<Stream> body)
    {
        var start = stream.Position;
        WriteUInt32(stream, 0);
        WriteAscii(stream, type);
        body(stream);
        var end = stream.Position;
        stream.Position = start;
        WriteUInt32(stream, checked((uint)(end - start)));
        stream.Position = end;
    }

    private static int ConvertAnnexBToAvcc(ReadOnlySpan<byte> annexB, byte[] destination)
    {
        var offset = 0;
        var written = 0;

        while (offset < annexB.Length)
        {
            var consumed = AnnexB.ReadNext(annexB[offset..], out var nalUnit);
            if (consumed <= 0)
            {
                break;
            }

            if (!nalUnit.IsEmpty)
            {
                if (written + 4 + nalUnit.Length > destination.Length)
                {
                    break;
                }

                BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(written), (uint)nalUnit.Length);
                nalUnit.CopyTo(destination.AsSpan(written + 4));
                written += 4 + nalUnit.Length;
            }

            offset += consumed;
        }

        return written;
    }

    private static void WriteAscii(Stream stream, string value)
    {
        Span<byte> buffer = stackalloc byte[4];
        Encoding.ASCII.GetBytes(value, buffer);
        stream.Write(buffer);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt24(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[3];
        buffer[0] = (byte)(value >> 16);
        buffer[1] = (byte)(value >> 8);
        buffer[2] = (byte)value;
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private struct Sample
    {
        public long Offset;
        public int Size;
        public long Dts;
        public int CompositionOffset;
        public bool Key;
    }
}
