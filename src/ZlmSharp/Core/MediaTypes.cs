namespace ZlmSharp.Core;

public enum TrackType
{
    Video = 0,
    Audio = 1,
}

/// <summary>编码 id（一期只有 H264，其余预留，避免后续改帧结构）。</summary>
public enum CodecId
{
    Unknown = 0,
    H264 = 1,
    H265 = 2,
    Aac = 3,
    Opus = 4,
    G711A = 5,
    G711U = 6,
}

[Flags]
public enum FrameFlags
{
    None = 0,

    /// <summary>SPS/PPS/AAC sequence header 等解码配置帧。</summary>
    Config = 1 << 0,

    /// <summary>关键帧（IDR）。</summary>
    Key = 1 << 1,

    /// <summary>需要优先发送。</summary>
    Boost = 1 << 2,

    /// <summary>缓冲紧张时可优先丢弃。</summary>
    Droppable = 1 << 3,
}

/// <summary>流标识：vhost/app/stream，对齐 ZLM 的 MediaSource 键。</summary>
public readonly record struct StreamKey(string Vhost, string App, string Stream)
{
    public const string DefaultVhost = "__defaultVhost__";

    public static StreamKey Create(string? vhost, string app, string stream) =>
        new(string.IsNullOrWhiteSpace(vhost) ? DefaultVhost : vhost.Trim(), app, stream);

    public override string ToString() => $"{Vhost}/{App}/{Stream}";
}
