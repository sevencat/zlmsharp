using System.Globalization;

namespace ZlmSharp.Configuration;

public enum ConfigValueKind
{
    String,
    Bool,
    Int,
    Long,
    Double,
}

/// <summary>一个可被 getServerConfig/setServerConfig 读写的配置项。</summary>
public sealed record ConfigKey(string Section, string Key, ConfigValueKind Kind, string Description)
{
    public string FullName => $"{Section}.{Key}";
}

/// <summary>
/// 配置键表：既是 setServerConfig 的白名单，也是类型校验依据。
/// 后续新增配置项时必须同步登记，避免"ini 里能写但 API 不认"。
/// </summary>
public static class ConfigSchema
{
    public static readonly IReadOnlyList<ConfigKey> All =
    [
        new("api", "apiDebug", ConfigValueKind.Bool, "是否打印 http api 调试日志"),
        new("api", "secret", ConfigValueKind.String, "敏感 api 调用 secret"),
        new("api", "snapRoot", ConfigValueKind.String, "截图保存根目录"),
        new("api", "defaultSnap", ConfigValueKind.String, "默认截图图片"),
        new("api", "downloadRoot", ConfigValueKind.String, "downloadFile 可访问根目录"),

        new("general", "enableVhost", ConfigValueKind.Bool, "是否启用虚拟主机"),
        new("general", "flowThreshold", ConfigValueKind.Int, "触发流量统计事件的阈值(KB)"),
        new("general", "maxStreamWaitMS", ConfigValueKind.Int, "等待流注册的最长时间(ms)"),
        new("general", "streamNoneReaderDelayMS", ConfigValueKind.Int, "无人观看延迟关闭时间(ms)"),
        new("general", "resetWhenRePlay", ConfigValueKind.Bool, "重新播放时是否重置时间戳"),
        new("general", "mergeWriteMS", ConfigValueKind.Int, "合并写毫秒"),
        new("general", "mediaServerId", ConfigValueKind.String, "媒体服务器 id"),
        new("general", "listen_ip", ConfigValueKind.String, "监听 ip，多个用逗号分隔"),
        new("general", "wait_track_ready_ms", ConfigValueKind.Int, "等待 track ready 时间(ms)"),
        new("general", "wait_audio_track_data_ms", ConfigValueKind.Int, "等待音频数据时间(ms)"),
        new("general", "wait_add_track_ms", ConfigValueKind.Int, "等待添加 track 时间(ms)"),
        new("general", "unready_frame_cache", ConfigValueKind.Int, "未就绪帧缓存数量"),
        new("general", "broadcast_player_count_changed", ConfigValueKind.Bool, "是否广播观看人数变化"),

        new("http", "port", ConfigValueKind.Int, "HTTP/API 端口"),
        new("http", "sslport", ConfigValueKind.Int, "HTTPS 端口"),
        new("http", "rootPath", ConfigValueKind.String, "HTTP 根目录"),
        new("http", "charSet", ConfigValueKind.String, "默认字符集"),
        new("http", "keepAliveSecond", ConfigValueKind.Int, "keepalive 超时(秒)"),
        new("http", "maxReqSize", ConfigValueKind.Int, "最大请求头大小(字节)"),
        new("http", "maxUploadSize", ConfigValueKind.Long, "最大上传大小(字节)"),
        new("http", "sendBufSize", ConfigValueKind.Int, "socket 发送缓冲(字节)"),
        new("http", "dirMenu", ConfigValueKind.Bool, "是否允许目录浏览"),
        new("http", "virtualPath", ConfigValueKind.String, "虚拟目录"),
        new("http", "forbidCacheSuffix", ConfigValueKind.String, "禁止缓存后缀"),
        new("http", "allow_origins", ConfigValueKind.String, "跨域允许来源"),
        new("http", "allow_ip_range", ConfigValueKind.String, "允许访问的 ip 段"),
        new("http", "forwarded_ip_header", ConfigValueKind.String, "取真实 ip 的请求头"),

        new("rtsp", "port", ConfigValueKind.Int, "RTSP 端口"),
        new("rtsp", "sslport", ConfigValueKind.Int, "RTSPS 端口"),
        new("rtsp", "authBasic", ConfigValueKind.Bool, "是否启用 basic 鉴权"),
        new("rtsp", "directProxy", ConfigValueKind.Bool, "是否直连转推"),
        new("rtsp", "handshakeSecond", ConfigValueKind.Int, "握手超时(秒)"),
        new("rtsp", "keepAliveSecond", ConfigValueKind.Int, "keepalive 超时(秒)"),
        new("rtsp", "lowLatency", ConfigValueKind.Bool, "是否低延迟模式"),
        new("rtsp", "rtpTransportType", ConfigValueKind.Int, "RTP 传输类型(-1 自动)"),

        new("rtmp", "port", ConfigValueKind.Int, "RTMP 端口"),
        new("rtmp", "sslport", ConfigValueKind.Int, "RTMPS 端口"),
        new("rtmp", "directProxy", ConfigValueKind.Bool, "是否直连转推"),
        new("rtmp", "handshakeSecond", ConfigValueKind.Int, "握手超时(秒)"),
        new("rtmp", "keepAliveSecond", ConfigValueKind.Int, "keepalive 超时(秒)"),
        new("rtmp", "enhanced", ConfigValueKind.Bool, "是否支持 enhanced rtmp"),

        new("protocol", "modify_stamp", ConfigValueKind.Int, "时间戳覆盖模式(0/1/2)"),
        new("protocol", "enable_audio", ConfigValueKind.Bool, "转协议是否开启音频"),
        new("protocol", "add_mute_audio", ConfigValueKind.Bool, "是否补静音音频"),
        new("protocol", "auto_close", ConfigValueKind.Bool, "无人观看是否直接关闭"),
        new("protocol", "continue_push_ms", ConfigValueKind.Int, "断流续推保护时间(ms)"),
        new("protocol", "paced_sender_ms", ConfigValueKind.Int, "平滑发送间隔(ms)"),
        new("protocol", "enable_hls", ConfigValueKind.Bool, "是否转 HLS"),
        new("protocol", "enable_hls_fmp4", ConfigValueKind.Bool, "是否转 HLS-fMP4"),
        new("protocol", "enable_mp4", ConfigValueKind.Bool, "是否录制 MP4"),
        new("protocol", "enable_rtsp", ConfigValueKind.Bool, "是否转 RTSP"),
        new("protocol", "enable_rtmp", ConfigValueKind.Bool, "是否转 RTMP/FLV"),
        new("protocol", "enable_ts", ConfigValueKind.Bool, "是否转 TS"),
        new("protocol", "enable_fmp4", ConfigValueKind.Bool, "是否转 fMP4"),
        new("protocol", "hls_demand", ConfigValueKind.Bool, "HLS 是否按需生成"),
        new("protocol", "rtsp_demand", ConfigValueKind.Bool, "RTSP 是否按需生成"),
        new("protocol", "rtmp_demand", ConfigValueKind.Bool, "RTMP/FLV 是否按需生成"),
        new("protocol", "ts_demand", ConfigValueKind.Bool, "TS 是否按需生成"),
        new("protocol", "fmp4_demand", ConfigValueKind.Bool, "fMP4 是否按需生成"),
        new("protocol", "mp4_max_second", ConfigValueKind.Int, "MP4 切片大小(秒)"),
        new("protocol", "mp4_save_path", ConfigValueKind.String, "MP4 保存路径"),
        new("protocol", "hls_save_path", ConfigValueKind.String, "HLS 保存路径"),

        new("log", "level", ConfigValueKind.Int, "日志级别(0trace~6off)"),
        new("log", "console", ConfigValueKind.Bool, "是否输出到控制台"),
        new("log", "file", ConfigValueKind.String, "日志文件路径，空则不写文件"),
        new("log", "maxFileSizeMB", ConfigValueKind.Int, "单日志文件大小上限(MB)"),
        new("log", "maxFiles", ConfigValueKind.Int, "日志文件保留个数"),
    ];

    private static readonly Dictionary<string, ConfigKey> Index = BuildIndex();

    public static ConfigKey? Find(string section, string key)
    {
        var fullName = $"{section}.{key}";
        return Index.TryGetValue(fullName, out var found) ? found : null;
    }

    public static bool TryValidate(string section, string key, string value, out string error)
    {
        var definition = Find(section, key);
        if (definition is null)
        {
            error = $"不支持的配置项: {section}.{key}";
            return false;
        }

        var ok = definition.Kind switch
        {
            ConfigValueKind.String => true,
            ConfigValueKind.Bool => TryParseBool(value),
            ConfigValueKind.Int => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            ConfigValueKind.Long => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            ConfigValueKind.Double => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            _ => false,
        };

        error = ok ? string.Empty : $"{section}.{key} 的值 '{value}' 不是合法的 {definition.Kind}";
        return ok;
    }

    private static bool TryParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "0" or "true" or "false" or "yes" or "no" or "on" or "off" => true,
        _ => false,
    };

    private static Dictionary<string, ConfigKey> BuildIndex()
    {
        var index = new Dictionary<string, ConfigKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in All)
        {
            index[key.FullName] = key;
        }

        return index;
    }
}
