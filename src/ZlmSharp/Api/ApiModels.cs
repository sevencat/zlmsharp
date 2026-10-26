namespace ZlmSharp.Api;

/// <summary>编译进程序的版本信息，对应 ZLM <c>/index/api/version</c> 的 data。</summary>
public sealed record ApiVersionInfo(string BuildTime, string BranchName, string CommitHash);

public sealed record ApiVersionData(string BuildTime, string BranchName, string CommitHash);

public sealed record ApiVersionResponse(int Code, ApiVersionData Data);

public sealed record ApiListResponse(int Code, IReadOnlyList<string> Data);

public sealed record ApiErrorResponse(int Code, string Msg);

/// <summary>一路在线流的信息（对应 ZLM getMediaList 的 data 元素，只保留核心字段）。</summary>
public sealed record ApiMediaInfo(
    string Vhost,
    string App,
    string Stream,
    string Schema,
    string OriginType,
    long AliveSecond,
    int ReaderCount,
    long TotalReaderCount,
    string VideoCodec,
    int VideoWidth,
    int VideoHeight,
    double VideoFps,
    string AudioCodec,
    int AudioSampleRate,
    int AudioChannels,
    long DropFrames);

public sealed record ApiMediaListResponse(int Code, IReadOnlyList<ApiMediaInfo> Data);

public sealed record ApiCloseStreamsResponse(int Code, int Count, bool Closed);

/// <summary><c>getMp4RecordFile</c>：录像根目录 + 文件/目录名列表。</summary>
public sealed record ApiRecordFileData(string RootPath, IReadOnlyList<string> Paths);

public sealed record ApiRecordFileResponse(int Code, ApiRecordFileData Data);

public sealed record ApiDeleteRecordFileData(int Count, IReadOnlyList<string> Paths);

public sealed record ApiDeleteRecordFileResponse(int Code, ApiDeleteRecordFileData Data);

/// <summary>一个在线会话（推流端或播放端）。</summary>
public sealed record ApiSessionInfo(
    string Id,
    string Schema,
    string Type,
    string Vhost,
    string App,
    string Stream,
    string Ip,
    int Port,
    long AliveSecond,
    string? UserAgent);

public sealed record ApiSessionListResponse(int Code, IReadOnlyList<ApiSessionInfo> Data);

public sealed record ApiKickSessionData(int Count, IReadOnlyList<string> Ids);

public sealed record ApiKickSessionResponse(int Code, ApiKickSessionData Data);

/// <summary>一个 YOLO 检测框（原图像素坐标，左上角 + 宽高）。</summary>
public sealed record ApiYoloObject(
    int ClassId,
    string ClassName,
    float Confidence,
    float X,
    float Y,
    float Width,
    float Height,
    long TrackId);

public sealed record ApiYoloDetectData(
    int Width,
    int Height,
    long ElapsedMs,
    int Count,
    IReadOnlyList<ApiYoloObject> Objects);

public sealed record ApiYoloDetectResponse(int Code, string? Msg, ApiYoloDetectData? Data);

public sealed record ApiYoloInfoData(
    bool Enabled,
    bool Loaded,
    string Model,
    string ModelDir,
    string Device,
    int InputWidth,
    int InputHeight,
    float Confidence,
    int ClassCount,
    string? Task,
    IReadOnlyList<string> ClassNames,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> LoadedModels);

public sealed record ApiYoloInfoResponse(int Code, string? Msg, ApiYoloInfoData? Data);

/// <summary>某路流最近一次 YOLO 检测结果。</summary>
public sealed record ApiYoloSnapshot(
    string Vhost,
    string App,
    string Stream,
    long At,
    long AgeMs,
    long ElapsedMs,
    int Width,
    int Height,
    int Count,
    IReadOnlyList<ApiYoloObject> Objects,
    string? Error);

public sealed record ApiYoloResultResponse(int Code, IReadOnlyList<ApiYoloSnapshot> Data);

/// <summary>一路叠加流（<c>{stream}_yolo</c>）的状态。</summary>
public sealed record ApiYoloOverlayData(
    string Vhost,
    string App,
    string Stream,
    string OverlayStream,
    string Input,
    string Output,
    string State,
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    long Frames,
    long StartedAt,
    long ElapsedMs,
    string? Error);

public sealed record ApiYoloOverlayResponse(int Code, string? Msg, ApiYoloOverlayData? Data);

public sealed record ApiYoloOverlayListResponse(int Code, IReadOnlyList<ApiYoloOverlayData> Data);

public sealed record ApiYoloOverlayFlagData(bool Flag);

public sealed record ApiYoloOverlayFlagResponse(int Code, ApiYoloOverlayFlagData Data);

/// <summary>一路录像的状态（路径是当前切片）。</summary>
public sealed record ApiRecordData(
    string Vhost,
    string App,
    string Stream,
    string State,
    bool Mp4,
    bool Flv,
    string? Mp4Path,
    string? FlvPath,
    long Frames,
    long DurationMs,
    long StartedAt,
    string? Error);

public sealed record ApiRecordResponse(int Code, string? Msg, ApiRecordData? Data);

public sealed record ApiRecordListResponse(int Code, IReadOnlyList<ApiRecordData> Data);

public sealed record ApiRecordFlagData(bool Flag);

public sealed record ApiRecordFlagResponse(int Code, ApiRecordFlagData Data);

/// <summary>一路 RTMP 转推的状态。</summary>
public sealed record ApiPushData(
    string Vhost,
    string App,
    string Stream,
    string Url,
    string State,
    long Frames,
    long StartedAt,
    long ElapsedMs,
    string? Error);

public sealed record ApiPushResponse(int Code, string? Msg, ApiPushData? Data);

public sealed record ApiPushListResponse(int Code, IReadOnlyList<ApiPushData> Data);

public sealed record ApiPushFlagData(bool Flag);

public sealed record ApiPushFlagResponse(int Code, ApiPushFlagData Data);

public sealed record ApiProxyKeyData(string Key);

public sealed record ApiProxyKeyResponse(int Code, ApiProxyKeyData Data);

public sealed record ApiProxyFlagData(bool Flag);

public sealed record ApiProxyFlagResponse(int Code, ApiProxyFlagData Data);
