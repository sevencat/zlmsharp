namespace ZlmSharp.Configuration;

/// <summary>
/// 缺省配置的唯一来源：首次启动时原样写入 <c>zlmsharp.ini</c>（保留注释），并作为解析基底。
/// 键名与 ZLM <c>conf/config.ini</c> 对齐；<c>[log]</c> 为 zlmsharp 扩展。
/// </summary>
public static class DefaultConfig
{
    public const string FileName = "zlmsharp.ini";

    public const string Text = """
        # zlmsharp 配置文件（缺省位于 exe 同目录，可用 -c 指定其他路径）
        # 键名与 ZLM config.ini 对齐，便于迁移；[log] 为 zlmsharp 扩展段。
        #
        # 注意：http.port 缺省为 8080（ZLM 为 80），避免 Windows 非管理员无法绑定低端口。
        # 部署时如需与 ZLM 完全一致，改为 80 并以管理员/root 运行。

        [api]
        # 是否打印每次 http api 请求内容与回复
        apiDebug=0
        # 敏感 api 需要 secret；来自 127.0.0.1/::1 的请求可免
        secret=035c73f7-bb6b-4889-a715-d9eb2d1925cc
        snapRoot=./www/snap/
        defaultSnap=./www/logo.png
        downloadRoot=./www

        [general]
        enableVhost=0
        flowThreshold=1024
        maxStreamWaitMS=15000
        streamNoneReaderDelayMS=20000
        resetWhenRePlay=1
        mergeWriteMS=0
        mediaServerId=your_server_id
        listen_ip=::
        wait_track_ready_ms=10000
        wait_audio_track_data_ms=1000
        wait_add_track_ms=3000
        unready_frame_cache=100
        broadcast_player_count_changed=0

        [http]
        port=8080
        sslport=443
        rootPath=./www
        charSet=utf-8
        keepAliveSecond=30
        maxReqSize=40960
        maxUploadSize=1073741824
        sendBufSize=65536
        dirMenu=1
        virtualPath=
        forbidCacheSuffix=
        allow_origins=*
        allow_ip_range=::1,127.0.0.1,172.16.0.0-172.31.255.255,192.168.0.0-192.168.255.255,10.0.0.0-10.255.255.255
        forwarded_ip_header=

        [rtsp]
        port=554
        sslport=0
        authBasic=0
        directProxy=1
        handshakeSecond=15
        keepAliveSecond=15
        lowLatency=0
        rtpTransportType=-1

        [rtmp]
        port=1935
        sslport=0
        directProxy=1
        handshakeSecond=15
        keepAliveSecond=15
        enhanced=1

        [protocol]
        modify_stamp=2
        enable_audio=1
        add_mute_audio=1
        auto_close=0
        continue_push_ms=15000
        paced_sender_ms=0
        enable_hls=0
        enable_hls_fmp4=0
        enable_mp4=0
        enable_flv_record=0
        enable_rtsp=1
        enable_rtmp=1
        enable_ts=0
        enable_fmp4=0
        hls_demand=0
        rtsp_demand=0
        rtmp_demand=0
        ts_demand=0
        fmp4_demand=0
        mp4_max_second=3600
        mp4_save_path=./www
        hls_save_path=./www

        [proxy]
        # 启动即拉的代理列表：app/stream=rtmp://... ; app/stream=rtsp://...
        enable=0
        max_concurrency=16
        retry_delay_ms=2000
        list=

        [hls]
        # startRecord?type=4 时输出 TS 切片 + hls.m3u8
        seg_duration=5
        seg_num=5

        [hook]
        # 媒体事件回调：推流/播放前 POST JSON 到这些地址，{"code":0} 放行，非 0 拒绝
        enable=0
        on_publish=
        on_play=
        on_stream_none_reader=
        timeoutSec=10
        retry=0
        retry_delay=1000
        # hook 不可用（超时/网络错误）时是否放行；0=拒绝（更安全）
        fail_open=0

        # zlmsharp 扩展段
        [log]
        # 0=trace 1=debug 2=info 3=warn 4=error 5=critical 6=off
        level=2
        console=1
        file=./log/zlmsharp.log
        maxFileSizeMB=20
        maxFiles=5
        """;
}
