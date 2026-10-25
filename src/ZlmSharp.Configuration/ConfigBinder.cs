namespace ZlmSharp.Configuration;

/// <summary>把 <see cref="IniDocument"/> 显式绑定为强类型配置（无反射，NativeAOT 安全）。</summary>
public static class ConfigBinder
{
    public static AppOptions Bind(IniDocument document)
    {
        var options = new AppOptions();
        var warnings = options.Warnings;

        var general = options.General;
        general.EnableVhost = document.GetBool("general", "enableVhost", general.EnableVhost);
        general.FlowThreshold = document.GetInt("general", "flowThreshold", general.FlowThreshold);
        general.MaxStreamWaitMS = document.GetInt("general", "maxStreamWaitMS", general.MaxStreamWaitMS);
        general.StreamNoneReaderDelayMS = document.GetInt("general", "streamNoneReaderDelayMS", general.StreamNoneReaderDelayMS);
        general.ResetWhenRePlay = document.GetBool("general", "resetWhenRePlay", general.ResetWhenRePlay);
        general.MergeWriteMS = document.GetInt("general", "mergeWriteMS", general.MergeWriteMS);
        general.MediaServerId = document.GetString("general", "mediaServerId", general.MediaServerId);
        general.ListenIp = document.GetString("general", "listen_ip", general.ListenIp);
        general.WaitTrackReadyMS = document.GetInt("general", "wait_track_ready_ms", general.WaitTrackReadyMS);
        general.WaitAudioTrackDataMS = document.GetInt("general", "wait_audio_track_data_ms", general.WaitAudioTrackDataMS);
        general.WaitAddTrackMS = document.GetInt("general", "wait_add_track_ms", general.WaitAddTrackMS);
        general.UnreadyFrameCache = document.GetInt("general", "unready_frame_cache", general.UnreadyFrameCache);
        general.BroadcastPlayerCountChanged = document.GetBool("general", "broadcast_player_count_changed", general.BroadcastPlayerCountChanged);

        var http = options.Http;
        http.Port = BindPort(document, "http", "port", http.Port, warnings);
        http.SslPort = BindPort(document, "http", "sslport", http.SslPort, warnings);
        http.RootPath = document.GetString("http", "rootPath", http.RootPath);
        http.CharSet = document.GetString("http", "charSet", http.CharSet);
        http.KeepAliveSecond = document.GetInt("http", "keepAliveSecond", http.KeepAliveSecond);
        http.MaxReqSize = document.GetInt("http", "maxReqSize", http.MaxReqSize);
        http.MaxUploadSize = document.GetLong("http", "maxUploadSize", http.MaxUploadSize);
        http.SendBufSize = document.GetInt("http", "sendBufSize", http.SendBufSize);
        http.DirMenu = document.GetBool("http", "dirMenu", http.DirMenu);
        http.VirtualPath = document.GetString("http", "virtualPath", http.VirtualPath);
        http.ForbidCacheSuffix = document.GetString("http", "forbidCacheSuffix", http.ForbidCacheSuffix);
        http.AllowOrigins = document.GetString("http", "allow_origins", http.AllowOrigins);
        http.AllowIpRange = document.GetString("http", "allow_ip_range", http.AllowIpRange);
        http.ForwardedIpHeader = document.GetString("http", "forwarded_ip_header", http.ForwardedIpHeader);

        var rtsp = options.Rtsp;
        rtsp.Port = BindPort(document, "rtsp", "port", rtsp.Port, warnings);
        rtsp.SslPort = BindPort(document, "rtsp", "sslport", rtsp.SslPort, warnings);
        rtsp.AuthBasic = document.GetBool("rtsp", "authBasic", rtsp.AuthBasic);
        rtsp.DirectProxy = document.GetBool("rtsp", "directProxy", rtsp.DirectProxy);
        rtsp.HandshakeSecond = document.GetInt("rtsp", "handshakeSecond", rtsp.HandshakeSecond);
        rtsp.KeepAliveSecond = document.GetInt("rtsp", "keepAliveSecond", rtsp.KeepAliveSecond);
        rtsp.LowLatency = document.GetBool("rtsp", "lowLatency", rtsp.LowLatency);
        rtsp.RtpTransportType = document.GetInt("rtsp", "rtpTransportType", rtsp.RtpTransportType);

        var rtmp = options.Rtmp;
        rtmp.Port = BindPort(document, "rtmp", "port", rtmp.Port, warnings);
        rtmp.SslPort = BindPort(document, "rtmp", "sslport", rtmp.SslPort, warnings);
        rtmp.DirectProxy = document.GetBool("rtmp", "directProxy", rtmp.DirectProxy);
        rtmp.HandshakeSecond = document.GetInt("rtmp", "handshakeSecond", rtmp.HandshakeSecond);
        rtmp.KeepAliveSecond = document.GetInt("rtmp", "keepAliveSecond", rtmp.KeepAliveSecond);
        rtmp.Enhanced = document.GetBool("rtmp", "enhanced", rtmp.Enhanced);

        var api = options.Api;
        api.Debug = document.GetBool("api", "apiDebug", api.Debug);
        api.Secret = document.GetString("api", "secret", api.Secret);
        api.SnapRoot = document.GetString("api", "snapRoot", api.SnapRoot);
        api.DefaultSnap = document.GetString("api", "defaultSnap", api.DefaultSnap);
        api.DownloadRoot = document.GetString("api", "downloadRoot", api.DownloadRoot);

        var protocol = options.Protocol;
        protocol.ModifyStamp = document.GetInt("protocol", "modify_stamp", protocol.ModifyStamp);
        protocol.EnableAudio = document.GetBool("protocol", "enable_audio", protocol.EnableAudio);
        protocol.AddMuteAudio = document.GetBool("protocol", "add_mute_audio", protocol.AddMuteAudio);
        protocol.AutoClose = document.GetBool("protocol", "auto_close", protocol.AutoClose);
        protocol.ContinuePushMS = document.GetInt("protocol", "continue_push_ms", protocol.ContinuePushMS);
        protocol.PacedSenderMS = document.GetInt("protocol", "paced_sender_ms", protocol.PacedSenderMS);
        protocol.EnableHls = document.GetBool("protocol", "enable_hls", protocol.EnableHls);
        protocol.EnableHlsFmp4 = document.GetBool("protocol", "enable_hls_fmp4", protocol.EnableHlsFmp4);
        protocol.EnableMp4 = document.GetBool("protocol", "enable_mp4", protocol.EnableMp4);
        protocol.EnableRtsp = document.GetBool("protocol", "enable_rtsp", protocol.EnableRtsp);
        protocol.EnableRtmp = document.GetBool("protocol", "enable_rtmp", protocol.EnableRtmp);
        protocol.EnableTs = document.GetBool("protocol", "enable_ts", protocol.EnableTs);
        protocol.EnableFmp4 = document.GetBool("protocol", "enable_fmp4", protocol.EnableFmp4);
        protocol.HlsDemand = document.GetBool("protocol", "hls_demand", protocol.HlsDemand);
        protocol.RtspDemand = document.GetBool("protocol", "rtsp_demand", protocol.RtspDemand);
        protocol.RtmpDemand = document.GetBool("protocol", "rtmp_demand", protocol.RtmpDemand);
        protocol.TsDemand = document.GetBool("protocol", "ts_demand", protocol.TsDemand);
        protocol.Fmp4Demand = document.GetBool("protocol", "fmp4_demand", protocol.Fmp4Demand);
        protocol.Mp4MaxSecond = document.GetInt("protocol", "mp4_max_second", protocol.Mp4MaxSecond);
        protocol.Mp4SavePath = document.GetString("protocol", "mp4_save_path", protocol.Mp4SavePath);
        protocol.HlsSavePath = document.GetString("protocol", "hls_save_path", protocol.HlsSavePath);

        var log = options.Log;
        log.Level = document.GetInt("log", "level", log.Level);
        log.Console = document.GetBool("log", "console", log.Console);
        log.File = document.GetString("log", "file", log.File);
        log.MaxFileSizeMB = document.GetInt("log", "maxFileSizeMB", log.MaxFileSizeMB);
        log.MaxFiles = document.GetInt("log", "maxFiles", log.MaxFiles);

        if (log.Level is < 0 or > 6)
        {
            warnings.Add($"[log] level={log.Level} 超出 0~6，已重置为 2(info)");
            log.Level = 2;
        }

        if (log.MaxFileSizeMB < 1)
        {
            log.MaxFileSizeMB = 20;
        }

        if (log.MaxFiles < 1)
        {
            log.MaxFiles = 5;
        }

        return options;
    }

    /// <summary>端口合法范围检查；0 表示该协议不监听。</summary>
    private static int BindPort(IniDocument document, string section, string key, int fallback, List<string> warnings)
    {
        var value = document.GetInt(section, key, fallback);
        if (value is < 0 or > 65535)
        {
            warnings.Add($"[{section}] {key}={value} 非法（0~65535），已使用默认值 {fallback}");
            return fallback;
        }

        return value;
    }
}
