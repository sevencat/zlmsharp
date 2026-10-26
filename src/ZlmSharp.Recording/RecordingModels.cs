namespace ZlmSharp.Recording;

/// <summary>录像格式（可组合）。</summary>
[Flags]
public enum RecordFormat
{
    None = 0,

    /// <summary>MP4（正式录像，收尾写 moov）。</summary>
    Mp4 = 1,

    /// <summary>FLV 原始档（追加式，崩溃可播）。</summary>
    Flv = 2,

    All = Mp4 | Flv,
}

public enum RecordingState
{
    Starting = 0,
    Recording = 1,
    Stopped = 2,
    Failed = 3,
}

/// <summary>一路录像的当前状态（路径是当前切片，切片后会自动更新）。</summary>
public sealed record RecordingStatus(
    string Vhost,
    string App,
    string Stream,
    RecordFormat Formats,
    RecordingState State,
    string? Mp4Path,
    string? FlvPath,
    DateTimeOffset StartedAt,
    long DurationMs,
    long Frames,
    string? Error)
{
    public bool Recording => State is RecordingState.Starting or RecordingState.Recording;
}

/// <summary>按 ZLM 习惯把 <c>startRecord.type</c> 映射为格式：1=MP4，2=FLV，3=两者。</summary>
public static class RecordFormats
{
    public static RecordFormat FromType(int type) => type switch
    {
        2 => RecordFormat.Flv,
        3 => RecordFormat.All,
        _ => RecordFormat.Mp4,
    };
}
