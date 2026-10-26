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
    int AudioChannels);

public sealed record ApiMediaListResponse(int Code, IReadOnlyList<ApiMediaInfo> Data);

public sealed record ApiCloseStreamsResponse(int Code, int Count, bool Closed);

/// <summary>一个 YOLO 检测框（原图像素坐标，左上角 + 宽高）。</summary>
public sealed record ApiYoloObject(
    int ClassId,
    string ClassName,
    float Confidence,
    float X,
    float Y,
    float Width,
    float Height);

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
