using ZlmSharp.Ai;

namespace ZlmSharp.Extra.Media;

/// <summary>
/// 外部媒体工具（ffmpeg / ffprobe）的统一入口。上层只依赖本接口，不接触命令行与进程，
/// 便于将来换成进程内直接调用 libav（ffmpeg dll）的实现。
/// </summary>
/// <remarks>
/// 当前实现 <see cref="FfmpegToolkit"/> 通过子进程 + 管道工作：
/// 解码输出 RGB24 原始帧，编码接收 RGB24 原始帧。实现方负责参数拼装、进程生命周期、stderr 采集。
/// </remarks>
public interface IMediaToolkit
{
    /// <summary>ffmpeg 可执行路径或命令名（DLL 实现可返回空串）。</summary>
    string FfmpegPath { get; }

    /// <summary>ffprobe 可执行路径或命令名（DLL 实现可返回空串）。</summary>
    string FfprobePath { get; }

    /// <summary>从任意 URL（RTSP/RTMP/HTTP-FLV）抓一帧 RGB24。</summary>
    /// <exception cref="TimeoutException">超时未出帧。</exception>
    /// <exception cref="InvalidOperationException">工具无法启动或执行失败。</exception>
    Task<RgbImage> GrabFrameAsync(string url, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>打开常驻解码：只保留最新一帧，读不到旧帧（用于分析采样，不会累积延迟）。</summary>
    IFrameSampler OpenSampler(string url, int width, int height);

    /// <summary>打开常驻解码：顺序输出每一帧（用于转码/叠加，不能丢帧）。</summary>
    IFrameReader OpenReader(string url, int width, int height);

    /// <summary>打开常驻编码：写入 RGB24 帧，推流或落盘。</summary>
    IFrameSink OpenSink(FrameSinkOptions options);
}

/// <summary>常驻解码的“最新一帧”视图（分析采样用）。</summary>
public interface IFrameSampler : IAsyncDisposable
{
    int Width { get; }

    int Height { get; }

    bool IsRunning { get; }

    /// <summary>最近一条 stderr（用于报错）。</summary>
    string LastError { get; }

    /// <summary>取最新一帧；超时或已退出返回 null。</summary>
    Task<RgbImage?> ReadFrameAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>常驻解码的顺序读视图（转码/叠加用）。</summary>
public interface IFrameReader : IAsyncDisposable
{
    bool IsRunning { get; }

    /// <summary>进程退出码；未退出为 null。</summary>
    int? ExitCode { get; }

    string LastError { get; }

    /// <summary>读满一整帧到 <paramref name="frame"/>；流结束返回 false。</summary>
    Task<bool> ReadIntoAsync(Memory<byte> frame, CancellationToken cancellationToken);
}

/// <summary>编码参数。</summary>
public sealed record FrameSinkOptions(
    string OutputUrl,
    int Width,
    int Height,
    int Fps,
    int BitrateKbps)
{
    /// <summary>关键帧间隔（帧数），默认 2 秒一个 GOP。</summary>
    public int GopSize { get; init; } = Fps * 2;
}

/// <summary>常驻编码：写入 RGB24 帧。</summary>
public interface IFrameSink : IAsyncDisposable
{
    bool IsRunning { get; }

    /// <summary>进程退出码；未退出为 null。</summary>
    int? ExitCode { get; }

    /// <summary>最近一条 stderr（用于报错）。</summary>
    string LastError { get; }

    Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);

    /// <summary>关闭输入并等待进程自然收尾（超时强制结束），返回退出码。</summary>
    Task<int?> CompleteAsync(TimeSpan timeout);

    /// <summary>等待进程退出（含超时兜底），返回退出码。</summary>
    Task<int?> WaitForExitAsync(TimeSpan timeout);
}
