namespace ZlmSharp.Core;

/// <summary>一条轨道（视频或音频）的元信息，随配置帧更新。</summary>
public sealed class MediaTrack
{
    private readonly object _gate = new();

    public MediaTrack(TrackType trackType)
    {
        TrackType = trackType;
    }

    public TrackType TrackType { get; }

    public CodecId CodecId { get; private set; } = CodecId.Unknown;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public double Fps { get; private set; }

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    /// <summary>H.264 SPS（Annex-B 原始字节，不含起始码）。</summary>
    public byte[]? Sps { get; private set; }

    /// <summary>H.264 PPS（Annex-B 原始字节，不含起始码）。</summary>
    public byte[]? Pps { get; private set; }

    /// <summary>收到过配置帧（可解码）后为 true。</summary>
    public bool Ready { get; private set; }

    public long LastDts { get; private set; }

    public void SetCodec(CodecId codecId)
    {
        lock (_gate)
        {
            CodecId = codecId;
        }
    }

    public void SetVideoSize(int width, int height, double fps = 0)
    {
        lock (_gate)
        {
            if (width > 0 && height > 0)
            {
                Width = width;
                Height = height;
            }

            if (fps > 0)
            {
                Fps = fps;
            }
        }
    }

    public void SetAudioInfo(int sampleRate, int channels)
    {
        lock (_gate)
        {
            if (sampleRate > 0)
            {
                SampleRate = sampleRate;
            }

            if (channels > 0)
            {
                Channels = channels;
            }
        }
    }

    public void SetH264Config(ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps)
    {
        lock (_gate)
        {
            Sps = sps.ToArray();
            Pps = pps.ToArray();
            Ready = true;
        }
    }

    public void UpdateFromFrame(MediaFrame frame)
    {
        lock (_gate)
        {
            CodecId = frame.CodecId;
            LastDts = frame.Dts;
            if (frame.IsConfig || frame.IsKeyFrame)
            {
                Ready = true;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            CodecId = CodecId.Unknown;
            Width = 0;
            Height = 0;
            Fps = 0;
            SampleRate = 0;
            Channels = 0;
            Sps = null;
            Pps = null;
            Ready = false;
            LastDts = 0;
        }
    }
}
