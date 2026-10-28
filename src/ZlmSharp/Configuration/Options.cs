namespace ZlmSharp.Configuration;

/// <summary>强类型配置根。字段与 ZLM 配置键一一对应，绑定过程无反射。</summary>
public sealed class AppOptions
{
    public GeneralOptions General { get; } = new();

    public HttpOptions Http { get; } = new();

    public RtspOptions Rtsp { get; } = new();

    public RtmpOptions Rtmp { get; } = new();

    public ApiOptions Api { get; } = new();

    public ProtocolOptions Protocol { get; } = new();

    public HookOptions Hook { get; } = new();

    public HlsOptions Hls { get; } = new();

    public ProxyOptions Proxy { get; } = new();

    public LogOptions Log { get; } = new();

    /// <summary>来自 <c>.env</c> 的机器相关配置（ffmpeg 路径等）。</summary>
    public EnvOptions Env { get; } = new();

    /// <summary>实际生效的配置文件绝对路径。</summary>
    public string ConfigPath { get; set; } = string.Empty;

    /// <summary>绑定过程中的容错提示（非法值、越界端口等），启动时打日志。</summary>
    public List<string> Warnings { get; } = new();
}

public sealed class GeneralOptions
{
    public bool EnableVhost { get; set; }

    public int FlowThreshold { get; set; } = 1024;

    public int MaxStreamWaitMS { get; set; } = 15000;

    public int StreamNoneReaderDelayMS { get; set; } = 20000;

    public bool ResetWhenRePlay { get; set; } = true;

    public int MergeWriteMS { get; set; }

    public string MediaServerId { get; set; } = "your_server_id";

    public string ListenIp { get; set; } = "::";

    public int WaitTrackReadyMS { get; set; } = 10000;

    public int WaitAudioTrackDataMS { get; set; } = 1000;

    public int WaitAddTrackMS { get; set; } = 3000;

    public int UnreadyFrameCache { get; set; } = 100;

    public bool BroadcastPlayerCountChanged { get; set; }
}

public sealed class HttpOptions
{
    public int Port { get; set; } = 8080;

    public int SslPort { get; set; } = 443;

    public string RootPath { get; set; } = "./WebUI";

    public string CharSet { get; set; } = "utf-8";

    public int KeepAliveSecond { get; set; } = 30;

    public int MaxReqSize { get; set; } = 40960;

    public long MaxUploadSize { get; set; } = 1073741824;

    public int SendBufSize { get; set; } = 65536;

    public bool DirMenu { get; set; } = true;

    public string VirtualPath { get; set; } = string.Empty;

    public string ForbidCacheSuffix { get; set; } = string.Empty;

    public string AllowOrigins { get; set; } = "*";

    public string AllowIpRange { get; set; } = string.Empty;

    public string ForwardedIpHeader { get; set; } = string.Empty;
}

public sealed class RtspOptions
{
    public int Port { get; set; } = 554;

    public int SslPort { get; set; }

    public bool AuthBasic { get; set; }

    public bool DirectProxy { get; set; } = true;

    public int HandshakeSecond { get; set; } = 15;

    public int KeepAliveSecond { get; set; } = 15;

    public bool LowLatency { get; set; }

    public int RtpTransportType { get; set; } = -1;
}

public sealed class RtmpOptions
{
    public int Port { get; set; } = 1935;

    public int SslPort { get; set; }

    public bool DirectProxy { get; set; } = true;

    public int HandshakeSecond { get; set; } = 15;

    public int KeepAliveSecond { get; set; } = 15;

    public bool Enhanced { get; set; } = true;
}

public sealed class ApiOptions
{
    public bool Debug { get; set; }

    public string Secret { get; set; } = string.Empty;

    public string SnapRoot { get; set; } = "./www/snap/";

    public string DefaultSnap { get; set; } = "./www/logo.png";

    public string DownloadRoot { get; set; } = "./www";
}

public sealed class ProtocolOptions
{
    public int ModifyStamp { get; set; } = 2;

    public bool EnableAudio { get; set; } = true;

    public bool AddMuteAudio { get; set; } = true;

    public bool AutoClose { get; set; }

    public int ContinuePushMS { get; set; } = 15000;

    public int PacedSenderMS { get; set; }

    public bool EnableHls { get; set; }

    public bool EnableHlsFmp4 { get; set; }

    public bool EnableMp4 { get; set; }

    /// <summary>是否同时录制 FLV 原始档（默认关闭；MP4 是正式录像）。</summary>
    public bool EnableFlvRecord { get; set; }

    public bool EnableRtsp { get; set; } = true;

    public bool EnableRtmp { get; set; } = true;

    public bool EnableTs { get; set; }

    public bool EnableFmp4 { get; set; }

    public bool HlsDemand { get; set; }

    public bool RtspDemand { get; set; }

    public bool RtmpDemand { get; set; }

    public bool TsDemand { get; set; }

    public bool Fmp4Demand { get; set; }

    public int Mp4MaxSecond { get; set; } = 3600;

    public string Mp4SavePath { get; set; } = "./www";

    public string HlsSavePath { get; set; } = "./www";
}

/// <summary>
/// 媒体事件钩子（对应 ZLM <c>[hook]</c>）：推/拉流前回调外部 HTTP 服务做鉴权，
/// 响应体 <c>{"code":0}</c> 表示放行，非 0 表示拒绝。
/// </summary>
public sealed class HookOptions
{
    public bool Enable { get; set; }

    /// <summary>推流鉴权地址（空表示不校验）。</summary>
    public string OnPublish { get; set; } = string.Empty;

    /// <summary>播放鉴权地址（空表示不校验）。</summary>
    public string OnPlay { get; set; } = string.Empty;

    /// <summary>无人观看时的回调地址（返回 0 表示允许关闭）。</summary>
    public string OnStreamNoneReader { get; set; } = string.Empty;

    /// <summary>hook 请求超时（秒）。</summary>
    public int TimeoutSec { get; set; } = 10;

    /// <summary>失败重试次数。</summary>
    public int Retry { get; set; }

    /// <summary>重试间隔（毫秒）。</summary>
    public int RetryDelayMS { get; set; } = 1000;

    /// <summary>hook 调用失败（超时/网络错误）时是否放行；默认关闭（拒绝，避免鉴权被绕过）。</summary>
    public bool FailOpen { get; set; }

    public bool HasAnyEndpoint =>
        OnPublish.Length > 0 || OnPlay.Length > 0 || OnStreamNoneReader.Length > 0;
}

/// <summary>HLS 录制（对应 ZLM <c>[hls]</c>）：TS 切片时长与保留片数。</summary>
public sealed class HlsOptions
{
    /// <summary>单个 TS 切片秒数（按关键帧对齐，实际可能略长）。</summary>
    public int SegDuration { get; set; } = 5;

    /// <summary>播放列表保留的切片数（更早的自动删除）。</summary>
    public int SegNum { get; set; } = 5;
}

/// <summary>
/// 拉流代理（对应自定义 <c>[proxy]</c> 段）：支持启动时按配置拉取，以及并发上限/重连间隔。
/// </summary>
public sealed class ProxyOptions
{
    public bool Enable { get; set; }

    /// <summary>同时拉流的路上限。</summary>
    public int MaxConcurrency { get; set; } = 16;

    /// <summary>断线重连间隔（毫秒）。</summary>
    public int RetryDelayMS { get; set; } = 2000;

    /// <summary>
    /// 启动即拉的列表：<c>app/stream=url</c>（或 <c>vhost/app/stream=url</c>），多项用 <c>;</c> 分隔。
    /// url 支持 <c>rtmp://</c> 与 <c>rtsp://</c>。
    /// </summary>
    public string List { get; set; } = string.Empty;
}

public sealed class LogOptions
{
    public int Level { get; set; } = 2;

    public bool Console { get; set; } = true;

    public string File { get; set; } = "./log/zlmsharp.log";

    public int MaxFileSizeMB { get; set; } = 20;

    public int MaxFiles { get; set; } = 5;
}
