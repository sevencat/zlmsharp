# zlmsharp 工作进度（workplan）

用 C#/.NET 10 复刻 ZLMediaKit 的核心能力：配置、媒体源分发、H.264 封装、HTTP-FLV / RTMP / RTSP 协议。
本文档记录里程碑进度与下一步计划，随每次功能落地更新。

## 里程碑

| 里程碑 | 内容 | 状态 |
| --- | --- | --- |
| M0 | 解决方案骨架、宿主（Kestrel + `UseZlmSharp`）、RTSP/RTMP 端口监听基类 | ✅ 完成 |
| M1 | 配置系统：`zlmsharp.ini` + `.env` 绑定、NLog、`/index/api/version`、`getApiList` | ✅ 完成 |
| M2 | 核心媒体层：`MediaFrame`（池化+引用计数）、`MediaSource`（环形缓冲/GOP/广播）、`MediaSink`、`MediaSourceRegistry` | ✅ 完成 |
| M3 | 编解码基础：Annex-B 起始码读写、SPS 解析（宽高/profile）、AVCC ↔ Annex-B、参数集解析器 | ✅ 完成 |
| M4 | FLV 封装 + HTTP-FLV 拉流（`/{vhost}/{app}/{stream}.live.flv`） | ✅ 完成 |
| M5 | RTMP：握手、chunk 流、AMF0、推流接入、播放（metadata + AVC sequence header + 帧转发） | ✅ 完成 |
| M6 | RTSP 播放：OPTIONS/DESCRIBE/SETUP/PLAY/PAUSE/TEARDOWN，TCP interleaved RTP（H.264 单包 + FU-A） | ✅ 完成 |
| M7 | WebSocket-FLV（与 HTTP-FLV 同路径升级）+ 媒体状态 API（`getMediaList`、`close_streams`） | ✅ 完成 |
| M8 | RTSP UDP 传输 / RTCP、PAUSE 续播与 `RTP-Info`、RTSP 推流、MP4/FLV 录制（带 AAC）、RTMP 转推、鉴权与 hook、`kick_session`、无人观看回收；二期：推流侧 RTCP RR + AAC 推流、HLS/TS 录制 + 录像文件 API + 断流续录 | ✅ 完成 |
| M9 | YOLO 目标检测（SimdYolo 纯托管推理）：`yoloInfo` / `yoloDetect` API、ffmpeg 抽帧上报 | ✅ 完成（一期：单图/单帧检测） |
| M10 | 拉流代理（远端 RTMP → 本地可播放）+ YOLO 定时检测（`addStreamProxy` / `delStreamProxy` / `getYoloResult`）；二期：ini `[proxy]` 启动即拉、RTSP 源、鉴权参数、`originType=proxy`、并发限流 | ✅ 完成 |
| M11 | YOLO 结果叠加：RGB24 绘框/标签 + ffmpeg 解码→绘制→编码叠加流（`startYoloOverlay` / `stopYoloOverlay` / `getYoloOverlay`） | ✅ 完成 |
| M12 | YOLO 轻量 IoU 跟踪：跨帧稳定 `trackId`、丢失容忍、叠加标签带 ID | ✅ 完成 |
| M13 | YOLO 多边形布防与告警：区域（多边形）命中用 `trackId` 关联、进出区域事件与告警回调 | 📋 规划 |
| M14 | YOLO 检测结果 WS 推送：按流订阅/退订、变化时推送快照（含 `trackId`） | 📋 规划 |
| M15 | H.265/HEVC 全链路：解析（VPS/SPS/PPS）、RFC 7798 RTP、FLV `hvcC`、MP4 `hvcC`、TS `0x24`、RTSP/RTMP 推播与转推 | ✅ 完成 |
| M16 | 管理页面：内置静态站点（零构建前端）+ 流/会话/录像/代理/转推/AI 可视化，复用 `/index/api/*` | ✅ 完成 |
| M17 | AI 通用层：`IStreamAnalyzer` / `IOverlayRenderer` / 通用结果模型，YOLO 成为首个实现，预留 OCR/车牌/布防 | ✅ 完成（通用调度待做） |
| M18 | 国标 GB/T 28181 接入：SIP 信令（REGISTER/MESSAGE/INVITE）+ PS over RTP 收流，平台侧最小闭环 | 📋 规划 |
| M19 | JT/T 809 接入：车辆注册/定位/报警报文接入与查询，与媒体流按设备号弱关联 | 📋 规划 |

## 已完成细节

### 媒体层（M2）

- `MediaFrame`：`ArrayPool` 租借 + 引用计数，读者必须 `Dispose`；`MediaFrame.Create` 拷贝输入。
- `MediaSource`：按帧数/时长裁剪的环形缓冲、GOP 快照（从最近关键帧起，带前置配置帧）、读者队列满则丢帧。
- `MediaSourceRegistry`：`GetOrCreate`/`Remove`/`CleanupIdle`，推流结束即移除并关闭所有观众。

### FLV / HTTP-FLV（M4）

- `FlvMuxer`：FLV header、`onMetaData`（AMF0 ECMA 数组，属性名为裸 key）、AVC sequence header、视频 tag（Annex-B → AVCC、composition time）。
- 对外暴露 `BuildHeader` / `BuildMetadataBody` / `BuildAvcConfigTag` / `WriteVideoBody` / `WriteVideoTag` / `TimestampOf`，RTMP/RTSP 播放共用。
- `HttpFlvEndpoint`：catch-all 路由只处理 `.live.flv`，等待流与 SPS/PPS 就绪后推 header + metadata + 配置帧 + GOP，随后增量转发。

### RTMP（M5）

- 协议库 `ZlmSharp.Protocols.Rtmp`（无第三方依赖）：AMF0 编解码、简单握手（服务端/客户端）、chunk 读写（fmt 0/1/2/3、扩展时间戳、Set Chunk Size/Abort、Ack 窗口）。
- `RtmpSession`：
  - 推流：`connect`/`createStream`/`publish`，FLV 音视频消息 → AVCC→Annex-B / 裸 AAC，回填 SPS/PPS/宽高，断流关流。
  - 播放：`play`，StreamBegin → `Play.Reset` → `Play.Start` → `onMetaData` → AVC sequence header → 帧转发；AAC 按 `0xAF` 组包；写入串行化，结束清理 reader。
- `RtmpListenerService` 接入 DI，透传握手/等流超时配置。

### RTSP（M6）

- 协议库 `ZlmSharp.Protocols.Rtsp`：
  - `RtspMessageReader` / `RtspResponse`：按行解析请求头、`Content-Length` 读 body，跳过客户端 interleaved RTCP；响应带 `CSeq`/`Server`/`Content-Length`。
  - `SdpBuilder`：H.264 SDP（`packetization-mode=1`、`profile-level-id`、`sprop-parameter-sets`、`a=control:trackID=0`）。
  - `RtpPacketizer`：12 字节 RTP 头（PT 96 / 90kHz / SSRC），小 NALU 单包、大 NALU FU-A 分片，每帧最后一包置 marker。
  - `RtspSession`：`OPTIONS`/`DESCRIBE`/`SETUP`（TCP interleaved 或 UDP）/`PLAY`（RTP-Info + 播放任务）/`PAUSE`/`TEARDOWN`/`GET_PARAMETER`；GOP 秒开、时间戳相对首帧（ms×90）、写入串行化、结束时移除 reader。
  - SETUP 传输协商：`RTP/AVP/TCP;interleaved=a-b` 走 TCP；`RTP/AVP;unicast;client_port=a-b` 走 UDP（`UdpRtpTransport` 分配随机 server_port，IPv4-mapped IPv6 先 `MapToIPv4`）；其它（含 `RAW/RAW/UDP`、缺 `client_port`）回 461。
  - RTCP SR（`RtcpSenderReport`，PT=200、28 字节、NTP+RTP 时间戳+包数/字节数）：首帧后立即发一次、之后按 `RtspSessionOptions.RtcpInterval`（默认 5s）发送，TCP 走 interleaved RTCP 通道、UDP 走 RTCP socket。
  - `RtspListenerService` 把 TCP 对端地址传给会话（UDP 目的 IP 来源）。
- `RtspListenerService` 接入 DI，握手/等流超时来自 `Rtsp.HandshakeSecond` 与 `General.*`。
- 参数集解析统一收敛到 `H264ConfigResolver`（HTTP-FLV / RTMP / RTSP 共用），去掉三份重复实现。

### WebSocket-FLV 与媒体 API（M7）

- `FlvLiveStream`：HTTP-FLV 与 WS-FLV 共用的分发循环（header → onMetaData → AVC sequence header → 视频 tag），写出通过委托抽象。
- `HttpFlvEndpoint`：同一路径 `/{vhost}/{app}/{stream}.live.flv` 根据 `IsWebSocketRequest` 分流；WS 分支逐条发二进制消息，结束发 Close 帧；`UseZlmSharp` 自动挂 `UseWebSockets`。
- 媒体 API：`getMediaList`（按 vhost/app/stream/schema 过滤，返回编码、宽高、帧率、读者数、存活秒数，宽度缺失时用 `H264ConfigResolver` 从 GOP 回填）、`close_streams`（按过滤条件关流，返回 `count`/`closed`）；`MediaSource` 增加 `CreatedAt` 供存活时长统计。

### RTSP 推流（M8）

- `RtspPushSession`（协议库）：`ANNOUNCE → SETUP → RECORD`。ANNOUNCE 用 `SdpParser` 取 H.264 payload/clock/`sprop-parameter-sets`；SETUP 同时支持 TCP interleaved 与 UDP（回 `server_port` + `ssrc`）；已存在发布者时回 453。
- RTP 收流：`H264RtpDepacketizer` 组帧（单包 / STAP-A / FU-A），按 `SequenceNumber` 去重并处理回绕；UDP 通道校验对端 IP。
- 时间戳：90kHz → ms，相对首包归零；时间戳回退/乱序按无重排处理，保证 DTS 单调。
- 参数集：SDP `sprop` 优先写入 `H264Config`，缺失时从带内首个 SPS+PPS 补齐；写完追加一条 `FrameFlags.Config` 帧供 GOP 秒开。
- `RtspListenerService` 按首条请求分流：`ANNOUNCE` 走推流会话，其余走播放会话；TEARDOWN/断连移除流。
- 限制：仅 H.264 视频，音频推流尚未支持。

### RTSP 续播与录制/转推（M8）

- RTSP PAUSE/续播：PAUSE 只停播放任务、保留会话；PLAY 从「上次发送的 DTS」继续（阅读器补发的更早 GOP 帧会被跳过），时间戳不重置；`Range: npt=x-` 可在环形缓冲范围内定位，响应 `Range: npt=<start>-`，`RTP-Info` 的 `seq` 取下一个包的序号、`rtptime` 与恢复后的首包一致；首次 PLAY 之后不再套用握手超时（PAUSE 期间靠 `GET_PARAMETER` 保活）。
- 录像（新项目 `ZlmSharp.Recording`）：
  - `Mp4Writer`：最小 ISO BMFF（ftyp + 64 位 mdat + moov 末尾），视频 AVCC（SPS/PPS → avcC）、音频 AAC（ASC → esds），stts/stss/ctts/stsc/stsz/stco|co64；按 DTS 算时长，B 帧用 ctts 表达；收尾回填 mdat 长度并写 moov。
  - `FlvFileWriter`：复用 `FlvMuxer`（新增音频：header flags 0x05、AAC sequence header、raw AAC tag），追加式写入，崩溃可播。
  - `RecorderManager`：每路流一个 reader，按 `max_second`（默认 `protocol.mp4_max_second`，夹在 10~86400 秒）切片，文件落在 `ZLM_RECORD_ROOT`（否则 `protocol.mp4_save_path`）下的 `app/stream/yyyy-MM-dd/HH-mm-ss-序号`；API `startRecord`（type=1 MP4 / 2 FLV / 3 两者）、`stopRecord`、`isRecording`；`protocol.enable_mp4` / `enable_flv_record` 打开时后台巡检自动录（跳过 `*_yolo`）。
  - 音频参数集从 `MediaTrack.AudioSpecificConfig` 取（`FlvFrameIngest` 收到 AAC sequence header 时写入）；源无音频时自动退化为纯视频录像。
- RTMP 转推（`RtmpPushClient` / `RtmpPushManager`）：客户端握手 → connect/createStream/publish → 发 onMetaData + AVC/AAC 参数集 → 逐帧发 FLV 音视频消息；chunk size 4096、独立 chunk stream、写锁串行化、窗口 ACK；API `startPush` / `stopPush` / `getPushList`；失败不自动重连（避免对着错地址空转）。
- 测试：RTSP PAUSE 续播 + Range 定位（seq/rtptime 对齐）；MP4/FLV 录制（stsz 采样数、avcC/esds、音频 flag、切片）；FLV 音频 tag；录像/转推 API 入参校验。实测腾讯流：MP4（H.264+AAC 1280x720，约 24.9s）与 FLV 都能完整解码，MP4 `-ss 15` 可定位；转推到本机 `live/tencent_copy` 后 H.264+AAC 在线，落成 FLV 完整解码无错误。

### 鉴权与 hook、会话管理（M8）

- 配置 `[hook]`（新增段，键名与 ZLM 对齐）：`enable` / `on_publish` / `on_play` / `on_stream_none_reader` / `timeoutSec` / `retry` / `retry_delay`，外加扩展项 `fail_open`（默认 0 = hook 不可用时拒绝，避免鉴权被绕过）。
- `MediaHookService`（Server/Hooks）：POST JSON 到外部服务，请求体字段同 ZLM（`mediaServerId`/`app`/`stream`/`vhost`/`schema`/`id`/`ip`/`port`/`user_agent`/`params`），额外带 `type`；响应 `{"code":0}` 放行，非 0 / 非 2xx / 超时按拒绝处理，支持重试。
- 按流鉴权：推/拉地址的查询参数（`?token=xxx`）原样放进 `params` 交给外部服务判定；拒绝时 RTMP 回 `NetStream.Publish.Failed`/`Play.Failed`、RTSP 回 403、HTTP-FLV 回 403。
- 接入点：RTMP publish/play、RTSP ANNOUNCE（推流）、RTSP DESCRIBE+PLAY（播放，每会话只鉴权一次）、HTTP-FLV / WS-FLV。
- `MediaSessionRegistry`（Core）：登记 `Id`/`Schema`/`Type`(publish|play)/`Key`/`Ip`/`Port`/`UserAgent`，保存取消回调；协议库各自持有会话级 `CancellationTokenSource`，踢人即取消该连接。
- API：`getSessionList`（支持 vhost/app/stream/schema/type 过滤）、`kick_session`（`id=` 精确踢，或按过滤条件批量踢，必须给 id 或至少一个条件）。

### 稳定性（M8）

- `IdleStreamCleanupService`（后台，2s 扫描一次）：超过 `general.streamNoneReaderDelayMS` 无人观看的流按下列规则处理 ——
  1. 没有推流会话的残留流（异常退出、代理断开）→ 直接回收；
  2. `protocol.auto_close=1` → 回收并踢掉推流端；
  3. 配了 `hook.on_stream_none_reader` → 由外部服务决定（返回 code 0 才关）；
  4. 其它情况保留（与 ZLM 默认一致，不会误伤长时间无人观看的推流）。
- `MediaSource`：新增 `LastReaderLeftAt`/`IdleSince`（空闲从最后一个读者离开算起）、`TotalDroppedFrames`/`TotalPendingFrames` 聚合。
- `MediaSourceRegistry.CleanupIdle` 改为按 `IdleSince` 判定，并支持 predicate 叠加条件。
- `getMediaList` 新增 `dropFrames`（该流所有观众队列累计丢帧），用于发现慢客户端。

### YOLO 检测（M9）

- 协议库 `ZlmSharp.Ai`（纯托管，无原生依赖）：`SimdYolo` 0.11.2（未上架 nuget.org，`.nupkg` 放在 `packages/` 并由根目录 `nuget.config` 作为本地源）。
- 配置全部走 `.env`（模型文件是机器相关的）：`ZLM_YOLO_MODEL_DIR`（模型目录）/`ZLM_YOLO_MODEL`（默认模型，只写文件名时到目录下找；空=关闭）/`ZLM_YOLO_DEVICE`（cpu/vulkan/metal/auto）/`ZLM_YOLO_CONFIDENCE`/`ZLM_YOLO_INPUT_WIDTH`/`ZLM_YOLO_INPUT_HEIGHT`/`ZLM_YOLO_CPU_THREADS`/`ZLM_YOLO_SAMPLE_INTERVAL_MS`；相对路径按 exe 目录解析。
- `YoloDetectorService`：首次使用某模型才加载（懒加载，失败缓存错误），用信号量串行化调用（SimdYolo 会话不可重入）；按模型路径缓存（默认最多 2 个，LRU 淘汰）；`DetectAsync` 返回原图坐标的框 + 类别名 + 耗时；`ReadModelSummary` 只读模型元信息（不加载后端）；`ResolveModelPath` 只接受目录内的文件名（拒绝路径与 `..`）。
- 多模型：`yoloDetect?model=yolov8m-person.onnx` 按请求选模型；`yoloInfo` 返回模型目录、可选模型列表、已缓存模型。
- 兼容性（本机 30 个 ONNX 实测）：检测/分割/姿态 OK；分类（`-cls`）与 OBB 不支持（head rank / `Cos` 算子），错误会带模型名原样返回。
- 图片输入：`RgbImage`（RGB24 + stride）+ `PpmImage`（P6 PPM 解析）。选 PPM 是因为零依赖，且 ffmpeg 能直接输出（`-f image2pipe -vcodec ppm`）。
- 取帧：`FfmpegFrameGrabber` 用 ffmpeg 从任意 URL（rtsp 走 tcp）抓一帧转 PPM，解决本项目没有内置 H.264 解码器的问题。仅用于 `yoloDetect?url=` 这种一次性按需检测；定时检测已改走常驻解码器（见 M10）。
- API：`GET /index/api/yoloInfo`（启用状态/模型/目录/设备/类别名/可选模型）、`POST /index/api/yoloDetect`（body=P6 PPM，或 `url=` 让 ffmpeg 抽帧；`model=` 选模型、`confidence=` 覆盖阈值），返回 ZLM 风格 `code/msg/data`。
- 本机配置（`.env`，不入库）：`ZLM_YOLO_MODEL_DIR=F:\ai\models\yolo`、`ZLM_YOLO_MODEL=yolov8s.onnx`，已验证 `yoloInfo` 列出 30 个模型、`yoloDetect` 用 `yolov8s.onnx` / `yolov8m-person.onnx` 实际出结果。

### 拉流代理与 YOLO 定时检测（M10）

- `RtmpPullClient`（协议库）：TCP 连接 → 简单握手 → `connect`/`createStream`/`play` → 循环收消息；支持 Window Ack 回执、`onStatus`/StreamEOF 结束判定、超时；FLV 负载交给 `FlvFrameIngest`。
- `FlvFrameIngest`：把推流侧的 FLV 解析（AVC sequence header / AVCC→Annex-B / AAC ASC）抽出来，服务端接收与拉流客户端共用（`RtmpSession` 已改为复用它）。
- `RtmpPullProxyManager`：`vhost/app/stream` + `rtmp://` 源地址 → 注册本地 `MediaSource`（schema=rtmp），断线 2s 重连；`Stop` 时移除本地流（观看者一起断开）。
- API：`addStreamProxy?vhost&app&stream&url`（返回 `data.key`，格式 `vhost/app/stream`）、`delStreamProxy?key` 或 `?vhost&app&stream`（返回 `data.flag`）。
- `StreamYoloMonitor`（后台任务）：每 `ZLM_YOLO_SAMPLE_INTERVAL_MS` 从常驻解码器取每路在线流的最新一帧 → `YoloDetectorService` 推理 → 写入 `YoloResultStore`（每路只留最近一次，信号量保证同刻只有一个推理）。
- 常驻解码器（替代「每次采样起一个 ffmpeg」）：`FfmpegLiveDecoder` 一路流一个 ffmpeg 子进程，按 `source.Video` 的宽高输出 `rgb24 rawvideo`，读循环始终读满整帧并只保留最新一帧（旧帧丢弃，延迟不累积），`LastError` 保留 stderr 末行；`FfmpegDecoderPool` 按 `StreamKey` 缓存，分辨率变化/进程退出自动重建，空闲 60s 或流结束时关闭。
- 注意：**不要**给这个子进程加 `-fflags nobuffer` / `-flags low_delay` —— 实测会让 ffmpeg 只出 0~1 帧；不累积延迟靠读循环丢旧帧保证。
- API：`getYoloResult?vhost&app&stream`（可省略过滤），返回 `at`/`ageMs`/`elapsedMs`/`width`/`height`/`count`/`objects`/`error`。
- 实测（腾讯 demo 流 `rtmp://liteavapp.qcloud.com/live/liteavdemoplayerstreamid`）：代理后本地 `rtmp://127.0.0.1:1935/live/tencent` 可被 ffprobe/ffplay 播放（H.264 1280x720 + AAC 48k），`getYoloResult` 持续更新，`yoloDetect?confidence=0.1` 在同一帧上报出 backpack/tv/motorcycle。

### YOLO 结果叠加（M11）

- `FrameOverlay`（纯托管绘制，无 System.Drawing/SkiaSharp 依赖）：在 RGB24 `Span<byte>` 上按 stride 画检测框 + 标签；内置 5×7 位图字体（0-9/A-Z/常用符号，小写转大写），按 `classId` 取 8 色稳定调色板；所有绘制都做边界裁剪，标签放不下时自动落回框内。
- `YoloOverlayManager` / `YoloOverlaySession`：每路叠加流起两个 ffmpeg 子进程——解码本地 HTTP-FLV（`-f rawvideo -pix_fmt rgb24`）→ C# 在每帧上画最近一次检测结果 → 编码（libx264/veryfast/zerolatency/yuv420p）推回本机 RTMP `{stream}_yolo`；宽高取源流 SPS 解析值并向下取偶（yuv420p 要求），帧率默认源帧率或 25。
- 检测结果复用 `YoloResultStore` 的最近快照（默认 5s 内有效），不对每帧推理；`StreamYoloMonitor` 跳过 `*_yolo` 流，避免对叠加结果再检测形成回路。
- API：`startYoloOverlay?vhost&app&stream&fps&bitrate&scale`、`stopYoloOverlay?vhost&app&stream`（返回 `data.flag`）、`getYoloOverlay`（返回 `state`/`frames`/`width`/`height`/`fps`/`error` 等）；起始要求源流在线且已有分辨率，`http.port`/`rtmp.port` 都必须开启。
- 实测（腾讯 demo 流 + `yolov8s.onnx`）：`startYoloOverlay?app=live&stream=tencent` → `tencent_yolo` 在线（H.264 1280x720），帧率为源码率 25fps 且无积压；抓帧可见黄色框 + `MOTORCYCLE 54%` 标签；`stopYoloOverlay` 后会话清空、`tencent_yolo` 从 `getMediaList` 消失。

### YOLO 轻量跟踪（M12）

- `ObjectTracker`：纯托管 IoU 跟踪，按类别做「IoU ≥ 0.3」的贪心匹配（候选按 IoU 降序占用，避免抢框）；未命中的轨迹容忍连续 5 次丢失，超过后丢弃且 ID 不复用；`Reset()` 用于分辨率变化/源重启。
- `StreamYoloMonitor` 按流持有跟踪器（`StreamKey → ObjectTracker`），分辨率变化时重建；每次采样写入 `YoloResultStore` 的是带稳定 `TrackId` 的框，`trackId` 仅用于展示/去抖，不做轨迹分析。
- `getYoloResult` / `yoloDetect` 的 `objects` 新增 `trackId`（单图检测恒为 0，因为没有跨帧上下文）；叠加标签格式变为 `#12 PERSON 87%`（无跟踪 ID 时不带前缀）。
- 测试：`ObjectTrackerTests` 覆盖跨帧 ID 稳定、同帧多目标 ID 分配、无重叠/跨类别换 ID、丢失容忍与过期回收、`Reset` 后 ID 从 1 重新开始。

### 拉流代理二期（M10 续）

- 配置化启动即拉：新增 `[proxy]` 段（`enable` / `max_concurrency` / `retry_delay_ms` / `list`）。`ProxyAutoStartService` 在宿主启动时按 `list` 起拉，每项形如 `app/stream=url` 或 `vhost/app/stream=url`，`;` 或换行分隔；解析不了或超并发上限的项只记 warning，不阻断启动。ini 的 `list` 支持行内 `;`（解析器仅把行首 `;`/`#` 当注释）。
- RTSP 源：`RtspPullClient`（DESCRIBE → SETUP → PLAY，**仅 TCP interleaved**，UDP 拉流对代理场景无收益且 NAT 问题多）：H.264 + AAC 双轨写入本地 `MediaSource`，25s `GET_PARAMETER` 保活、结束发 `TEARDOWN`、收到对端 SR 回 RR。
- 鉴权参数：`rtsp://user:pass@host/...` 的 UserInfo 生成 `Authorization: Basic`，DESCRIBE/SETUP/PLAY/保活/TEARDOWN 全程携带；**只支持 Basic，不支持 Digest**。
- 来源标注：代理流 `originType=proxy`，`schema` 跟随源协议（rtsp 源 → `schema=rtsp`，此前硬编码 `rtmp`），`getMediaList` / `getSessionList` / hook 载荷都能看到。
- 并发限流：`max_concurrency`（夹在 1~256）超限抛 `InvalidOperationException("拉流代理已达并发上限（N）")`；同一路重启不额外占额；断线按 `retry_delay_ms`（200ms~60s）自动重连，`Stop`/`DisposeAsync` 负责收敛。
- 测试：`RtspPullProxyTests`（RTSP 推流 → 代理拉流 → 本地出帧；`[proxy] enable=1` 启动即拉 + 源流后到靠重连接上）、`ProxySecondPhaseTests`（list 两种键形式解析、非 rtmp/rtsp URL 拒绝、并发上限与同路重启、`originType`/`schema`）。

### 录制二期（M8 续）

- `TsMuxer`：最小 MPEG-TS 封装（PAT/PMT + H.264 PES `0xE0` + AAC/ADTS PES `0xC0`，188 字节对齐，PCR 挂视频 PID）。
- `HlsPackage`：关键帧对齐的滚动切片，切片 `hls-000001.ts` 递增、列表 `hls.m3u8`（`TARGETDURATION`/`MEDIA-SEQUENCE`/`EXTINF`/`ENDLIST`），超出保留数删除最旧片；目录 `{root}/{app}/{stream}/hls`。
- 配置：`[hls] seg_duration`（1~60s）/ `seg_num`（2~100）；`startRecord?type=4` 走 HLS、`type=5` 表示「全部格式」。注意 `RecordFormat.All` 只含 MP4|FLV，HLS 需显式指定。
- 录像文件 API：`getMp4RecordFile`（按 `app/stream/period/path` 列日期目录或文件）、`deleteRecordFile`（`path` 或 `app+stream+file_name`，返回 `count` 与已删列表）；两者都用 `GetFullPath` 前缀校验，越界路径直接拒绝。
- 断流续录：`RecorderManager` 记录期望的录制计划，`RecorderAutoStartService` 每 2s 巡检，流重新上线（有 SPS）即自动重开；同一巡检顺带按 `protocol.enable_mp4` / `enable_flv_record` 自动录（跳过 `*_yolo`），自动录**不含 HLS**。
- 测试：`HlsRecorderTests`（TS 包对齐/PSI/PES、ADTS、切片滚动与保留数、非关键帧不建片、录像文件列出/删除/越界拒绝）、`RecordingPipelineTests`（`type=4` 产出 m3u8+ts、断流重推 15s 内自动续录、录像文件 API 端到端）。

### RTSP 推流二期：推流侧 RTCP 与 AAC（M8 续）

- 推流侧 RTCP：`RtcpReceiverReport`（PT=201、32 字节、单 report block、24 位丢包计数钳制）按 `RtspSessionOptions.RtcpInterval`（默认 5s）对每个已收到数据的 track 回发；TCP 走 interleaved `$` 帧、UDP 走 RTCP socket。对端 SR 会被解析（记录 NTP middle 供 DLSR），但**推流侧自身不发 SR**。
- AAC 推流：ANNOUNCE 的 SDP 同时解析视频轨与 `MPEG4-GENERIC` 音频轨，音频 `pt`/`ASF` 写入 `MediaTrack.AudioSpecificConfig`；`AacRtpDepacketizer` 按 RFC 3640（AAC-hbr）解包，多 AU 包只取第一个并累计 `SkippedAuCount`。H.265 仍拒绝。
- 测试：`RtspPushAudioTests`（双轨 SDP 解析、纯视频解析、H265 拒绝、AAC 单/多 AU 解包、RR 布局与负丢包钳制、SR 解析、真实推流会话写入音频轨并收到 PT=201 的 RR）。

## 工程结构

解决方案只保留 4 个项目：

- `src/ZlmSharp`：唯一类库（程序集 `ZlmSharp.MediaServer`；exe 名固定 `zlmsharp`，程序集不能与它大小写不敏感重名），按目录分层：`Core` / `Codecs`（H.264 + H.265）/ `Configuration` / `Protocols/{Flv,Rtmp,Rtsp}` / `Http` / `Recording` / `Ai`（含 `Ai/Analysis` 通用分析层）/ `Api` / `Server` / `WebUI`（管理页面，嵌入资源），命名空间保持原样，便于对照 ZLM 源码。
- `src/ZlmSharp.Cli`：可执行入口，产物 `zlmsharp.exe`（NativeAOT 发布）。
- `samples/ZlmSharp.Sample.Web`：嵌入模式示例。
- `tests/ZlmSharp.Tests`：xUnit 单元/端到端测试。
- `assets/`：`app.ico`（exe 图标，16/24/32/48/64/128/256 多尺寸）与 `app-icon.png`（README 展示图），由 `tools/make-icon.ps1` 纯 System.Drawing 绘制生成；`Cli` 通过 `<ApplicationIcon>` 引用，JIT 与 NativeAOT 产物都会带上 Win32 图标。

## 测试基线

`dotnet test tests/ZlmSharp.Tests`：**259 个用例全绿**，`dotnet build zlmsharp.sln` Debug/Release 均 0 警告。

三期新增覆盖：HEVC SPS/hvcC 往返（真实参数集）、RFC 7798 单包/FU 往返、H.265 SDP 生成与解析、FLV/MP4/TS 的 HEVC 封装、RTSP H.265 推流→播放端到端、通用分析层（结果存储/图元映射/三种 renderer/分析器注册表/伪分析器链路）、管理页面（内嵌资源、路由不遮蔽 API 与 FLV）。

覆盖：配置绑定、StreamUrl 解析、媒体源/GOP/注册表、H.264 解析与 AVCC 互转、FLV 封装、HTTP-FLV（端到端）、AMF0、RTMP chunk、RTMP 握手（S1 版本字段）、RTMP 推流会话、RTMP 播放会话、RTMP 推流 → HTTP-FLV 端到端、拉流代理端到端（推流 → 代理 → 本地播放）、**RTSP 源拉流代理端到端、`[proxy]` 启动即拉（含源流后到重连）、代理 list 解析/并发上限/`originType`+`schema`**、RTSP 消息解析、SDP、RTP 打包、RTCP SR、**推流侧 RTCP RR 与 SR 解析、AAC 推流解包**、RTSP 播放会话（TCP / UDP）、RTSP 监听服务端到端（TCP / UDP）、RTSP PAUSE 续播与 Range 定位（seq/rtptime）、RTSP 推流（SDP 解析 / RTP 解包 / 目标 URI）、WS-FLV 端到端、getMediaList/close_streams、PPM 解析/显式 YOLO 配置、yoloInfo/yoloDetect（未启用/模型不可读/入参校验）、RGB24 绘框与标签（裁剪/stride/位图字体）、叠加流参数校验与叠加入口、IoU 跟踪（ID 稳定/丢失容忍/过期回收）、FLV 音频 tag、MP4/FLV 录制（stsz 采样数、avcC/esds、音频、切片）、**HLS/TS 录制（包对齐/PSI/PES/ADTS/切片滚动/非关键帧不建片）、录像文件 API（列出/删除/越界拒绝）、断流自动续录**、录像/转推 API 入参、hook（请求体字段/code 拒绝/HTTP 错误/超时/fail_open/RTMP 推流放行与拒绝）、会话注册表与 getSessionList/kick_session（含踢掉推流端）、无人观看回收（auto_close 开/关、残留流兜底、跨周期不误杀）、丢帧统计与 `dropFrames`、常驻解码器（启动失败路径、真实 ffmpeg 解码与池释放、连续取帧）。

真实模型冒烟（不进常规 CI）：`ZLM_YOLO_TEST_MODEL=<本地 .pt|.onnx> dotnet test --filter YoloModelSmokeTests`，已验证 yolov8n / `F:\ai\models\yolo` 下的 ONNX 加载 → 推理 → 结果映射跑通（COCO 80 类）。

## 近期收尾（M8/M10 范围内）

1. YOLO 三期：多边形布防与告警（区域命中按 `trackId` 关联，见 M13）、检测结果 WS 推送（按流订阅 + 快照推送，见 M14）、叠加流按需自动跟随（有人看才起）、叠加流的双 ffmpeg 管道也改用常驻解码器；GPU（Vulkan）验证与 NativeAOT 实测。（**FFmpeg.AutoGen 已评估后暂缓**：8.1.0 可对接本机 FFmpeg 8.1，但 x264 的 GPL 传染、原生库崩溃会带崩进程、~190MB 按 RID 分发、AOT/裁剪不友好；先做常驻子进程解码器，进程内 libav* 以后再做。）
2. RTSP 推流收尾：推流侧 SR（当前只回 RR，作为纯接收方）、多音频轨。
3. HLS 直播转协议：目前 HLS 只用于录制（`startRecord?type=4`），`protocol.enable_hls` / `hls_demand` 仅完成配置绑定，尚未接线到播放侧；自动录也还没有 HLS 选项。
4. 录制与运维增强（可选）：录制文件按总容量/保留天数自动清理（当前只按切片数滚动 HLS）、MP4 边录边写以支持崩溃恢复（当前收尾才写 moov）、Prometheus 指标（在线流/读者/丢帧/代理重连次数）、日志按天归档（当前按大小滚动 + `maxFiles` 兜底）。

## 下一阶段计划（三期，M15–M19）

**进展**：P1（H.265）、P2（管理页面）、P3（AI 通用层）已落地，259 个用例全绿；**下一步只剩 P4（国标 / JT809）**，建议拆成「国标」「JT809」两个子迭代。下面各节保留原始设计说明，并在标题下补了「落地结果 / 仍未做」。

### P1（M15）H.265 / HEVC 全链路支持 ✅ 已完成

**落地结果**：`Codecs/H265{NalUnit,SpsParser,Config}` + `VideoCodecConfig`（H.264/H.265 统一视图）+ `VideoCodecConfigResolver` 把原先散在各 muxer 的 codec 分支收成一处；RTSP 按 RFC 7798 打包/解包（`H265RtpDepacketizer`、`IVideoRtpDepacketizer`），SDP 带 `sprop-vps/sps/pps`；FLV/RTMP 用 codecId 12 + `hvcC`（采集与播放双向），MP4 写 `hvc1` + `hvcC`，TS/PMT 用 stream_type `0x24`，HLS/录像/转推随之生效。RTSP 推流→播放、FLV/MP4/TS 采集均有用例，SPS 解析与 hvcC 往返用真实 ffmpeg/libx265 参数集对拍（320x240 / level 60）。

**仍未做**：RTMP 播放侧只写 legacy codecId 12 + `hvcC`（未实现 enhanced RTMP 的四字节扩展头）；MP4 固定 `hvc1`（未提供 `hev1` 开关）；H.265 叠加流未在真实 265 源上做端到端验证（解码走 ffmpeg 子进程，理论上与 H.264 一致）。

**现状**：`CodecId.H265` 与 `getMediaList` 的 `"H265"` 字符串只是占位；`MediaTrack` 只有 `SetH264Config`；H.264 专用实现散在 `H264SpsParser` / `H264ConfigResolver` / `FlvMuxer`（AVC + `avcC`）/ `RtpPacketizer`（RFC 6184）/ `H264RtpDepacketizer` / `SdpBuilder` / `TsMuxer`（stream_type 0x1B）/ `Mp4Writer`（只写 `avcC`，注释已声明不支持 HEVC）/ `RtspPushSession` / `RtspPullClient`；RTSP SDP 解析现在明确拒绝 H265（有对应用例）。

**范围（按链路）**：

1. 解析层：NAL 双字节头类型解析、VPS/SPS/PPS 提取、`H265SpsParser`（profile_tier_level + conformance window → 宽高/profile）、`H265Config` 与 `H265ConfigResolver`；Annex-B 切分可复用现有 `AnnexB`。
2. 媒体层：`MediaTrack.SetH265Config`（VPS+SPS+PPS）与配置帧写入；`MediaFrame` 的 codec 标注贯通。
3. 入站：RTSP ANNOUNCE 解析 `H265/90000` + `sprop-vps/sps/pps`，RTP 按 **RFC 7798** 解包（单包 / AP / FU，2 字节 payload header）；RTMP 侧 FLV `codecId=12` + `hvcC`（优先 enhanced RTMP 的四字节 video header，legacy sequence header 兜底）。
4. 出站：RTSP 播放（SDP + RFC 7798 打包）、HTTP-FLV / WS-FLV（`hvcC` + composition time）、MP4（`hvc1` + `hvcC` box）、HLS/TS（stream_type `0x24` + HEVC descriptor）、RTMP 转推。
5. 复用侧：`RtspPullClient` 按 SDP 选择解包器（拉流代理因此自动支持 HEVC 源）；YOLO/叠加走 ffmpeg 子进程，只需把 codec 透传过去，无需改解码逻辑。

**设计要点**：把上述四处硬编码收成 `IVideoCodecProfile`（`CodecId` + 参数集 + RTP 打包/解包 + 各封装的参数集写出），各 muxer 只依赖接口，今后接 AV1/H.266 不再散弹式修改。

**验收**：单测覆盖—H265 SPS 解析宽高、RFC 7798 单包/AP/FU 解包、FLV `hvcC`、MP4 `hvcC` box、TS PMT `0x24`、RTSP H265 推流→播放端到端；实测—`ffmpeg -c:v libx265` 推流后 RTMP / HTTP-FLV / RTSP / MP4 / HLS 均可播。

**风险**：enhanced RTMP 兼容性（老客户端只认 `codecId=12` legacy 头）；MP4 `hvc1` vs `hev1` 标签选择；浏览器对 HEVC 的支持差异（管理页面预览需回落到 FLV+扩展或提示）。

### P2（M16）管理页面 ✅ 已完成

**落地结果**：`WebUI/{index.html,app.js,style.css}` 作为嵌入资源打进 `ZlmSharp.MediaServer`，`AdminPageEndpoints` 映射 `GET /` 与 `GET /admin/{file}`（`http.rootPath` 下同名文件优先，便于热更新）；页面零构建、直接调用 `/index/api/*`，覆盖概览、在线流、预览播放（HTTP-FLV）、会话踢人、录像查询、拉流代理/转推、AI 结果。鉴权复用 `api.secret`（存 localStorage），路由只占 `/` 与 `/admin/*`，不遮蔽 API 与 FLV catch-all（有用例）。

**仍未做**：`adminAuthBasic` 未加（目前仅 secret）；页内播放依赖 mpegts.js CDN，纯离线环境需把库放到 `www/`；没有配置编辑与日志查看面板。

**现状**：`http.rootPath` / `dirMenu` 只是配置项——`UseZlmSharp` 目前**没有** `UseStaticFiles`，不存在任何页面；API 侧已覆盖流/会话/录像/代理/转推/YOLO，够页面直接用。

**范围**：内置静态站点（`www/` 目录优先，缺失时回落程序集内嵌资源）+ 复用 `/index/api/*`：
- 概览：版本/端口/在线流数/会话数/丢帧汇总；
- 流列表：`getMediaList` + 预览播放器（HTTP-FLV，flv.js / mpegts.js 二选一）；
- 会话：`getSessionList` / `kick_session`；
- 录像：`startRecord` / `stopRecord` / `isRecording` / `getMp4RecordFile` / `deleteRecordFile`；
- 代理与转推：`addStreamProxy` / `delStreamProxy` / `startPush` / `getPushList`；
- AI：`getYoloResult` 快照 + 叠加开关；
- 配置：只读展示当前 ini（敏感键打码）。

**设计要点**：前端零构建（原生 ES module + 少量 JS，产物入库），不引入 Node 工具链；鉴权复用 `api.secret`（页面首次输入存 localStorage，请求自动带 `secret`）并新增可选 `[http] adminAuthBasic`（Basic 保护整站，回环免）；路由上媒体路径（`*.live.flv`、`/index/api/*`、WS 升级）必须优先匹配，静态资源只兜底 `/`。

**验收**：`dotnet publish` 后 `http://host:8080/` 可打开概览并播放一路流；`www` 缺失走内嵌资源；非回环无 secret 时返回 401/登录页。用例：静态兜底不遮蔽 API 与 FLV 路由、admin Basic 鉴权分支、内嵌资源命中。

**风险**：SPA 兜底与媒体 catch-all 路由冲突（`HttpFlvEndpoint` 本身就是 catch-all，需要顺序与后缀双重约束）；AOT 下静态资源要显式声明为嵌入资源。

### P3（M17）AI 分析与叠加通用层（YOLO → 可插拔，预留 OCR 等）✅ 已完成

**落地结果**：`Ai/Analysis/` 新增 `AnalysisPrimitive`（Box/Polygon/Text/Mask）、`AnalysisResult`、`AnalysisResultStore`（按流 + 分析器）、`IStreamAnalyzer` + `AnalyzerRegistry`、`IOverlayRenderer` + `BoxOverlayRenderer`/`PolygonOverlayRenderer`/`TextOverlayRenderer` + `OverlayComposer`；`YoloAnalyzer` 把检测 + IoU 跟踪适配成通用图元，`StreamYoloMonitor` 的定时结果同时写入通用存储；新增 `analyzeResult`（支持 `analyzer=` 过滤），`getYoloResult` 字段保持兼容。`FrameOverlay` 补 `DrawLine`，多边形/OCR 文本框可直接叠加。

**仍未做**：定时调度仍由 YOLO 专用 `StreamYoloMonitor` 驱动，尚未改成「遍历 `AnalyzerRegistry`」的通用调度器；`ZLM_AI_*` 配置前缀未接线（YOLO 仍读 `ZLM_YOLO_*`），等第二个分析器（OCR）落地时一并收口。

**现状**：`Ai/` 全部以 YOLO 命名——`YoloDetectorService`（模型加载/推理）、`YoloResultStore`（按流存快照）、`StreamYoloMonitor`（定时采样）、`YoloOverlayManager` / `YoloOverlaySession`（decode→draw→encode 双 ffmpeg）、`FrameOverlay`（只画矩形框 + 内置位图字体）、`ObjectTracker`（IoU）。其中 `FfmpegDecoderPool` / `FfmpegLiveDecoder` 本就是通用取帧设施，只是挂了 YOLO 名字。

**目标分层**：

1. 取帧层（去 YOLO 化，重命名沿用）：`IFrameSource`——按流取最新 RGB24 帧，常驻解码器池，分辨率变化自愈。
2. 分析层：`IStreamAnalyzer`（`AnalyzeAsync(FrameSample, ct) → AnalysisResult`），首个实现 `YoloAnalyzer`（检测/姿态/分割），后续 `OcrAnalyzer`（文本检测 + 识别）、`LprAnalyzer`（车牌）等；`AnalyzerRegistry` 决定每路流开哪些分析器，`StreamAnalyzerMonitor` 统一调度与限频。
3. 叠加层：`IOverlayRenderer`（把 `AnalysisResult` 画到 RGB 帧），已有框渲染器改名 `BoxOverlayRenderer`，新增 `TextOverlayRenderer`（OCR 多边形/文本）、`PolygonOverlayRenderer`（布防区域，顺带覆盖 M13）。
4. 数据模型：`AnalysisResult` 由通用图元 `AnalysisPrimitive`（`Box` / `Polygon` / `Text` / `Mask` + `Label` / `Score` / `TrackId` / `Attributes`）组成；`YoloResult` 变为其特化适配，`getYoloResult` 字段保持兼容，新增 `analyzeResult?analyzer=`。

**配置**：`.env` 的 `ZLM_YOLO_*` 收敛为 `ZLM_AI_*`（旧名保留兼容），按 analyzer 分段给模型/设备/阈值/采样间隔。

**验收**：现有 238 用例零回归、旧 API 字段不变；新增用例——`AnalysisPrimitive` 组合、`TextOverlayRenderer` 不越界、`analyzeResult` 过滤、同流双分析器并存的叠加合成；OCR 先用占位实现跑通管道，模型选型（PaddleOCR ONNX 本地推理 vs 云 API）单独评估后再替换。

**风险**：改动面广但机械（约 15 个文件 + API + 配置），容易在兼容层留双份结构；OCR 如果不追求本地推理，可以先用「外部 HTTP 分析器」适配器，避免又背一个原生依赖。

### P4（M18/M19）国标 GB/T 28181 与 JT/T 809 接入

**现状**：无 SIP、无 PS 解复用；`Jt809` 相关能力为零（也没有数据库）。好消息是 `MediaSource` 只认「H.264/AAC 帧」，接入层只要能拆成 `MediaFrame` 就能复用播放/录像/转推/代理/YOLO 全链路。

**GB/T 28181（M18，先做平台侧）**：

1. SIP 信令（UDP + TCP）：`REGISTER`（Digest 鉴权）、`MESSAGE`（Keepalive / Catalog 查询与响应）、`INVITE`/`ACK`/`BYE`（实时点播）；设备侧与平台侧先做一侧——平台侧：设备注册后由本服务 `INVITE`，让设备把 PS 流推到本机。
2. 媒体接入：RTP 收流端口池（新增 `general.rtpPortRange`），`PsDemuxer` 解析 PS（`0xE0` 视频 → H.264/H.265 Annex-B，`0xBD` 私有流 → G.711/AAC），按 SSRC 关联 SIP 会话后写入 `MediaSource`。
3. 能力面：`getMediaList` 的 `originType=gb28181`；设备/通道列表 API；录像回放（INVITE + 回放控制，二期）。
4. 设计要点：新增 `src/ZlmSharp/Protocols/Gb28181/`，SIP 编解码自己写（复用 `RtspMessageReader` 的「按行头 + Content-Length」思路，避免引入重量级 SIP 库）；PS 解复用做成纯字节流 → 帧序列的函数，单测不依赖网络。

**JT/T 809（M19，非媒体协议）**：

1. 首期只做「接入侧」：TCP 长连接 + 报文头（`0x5B`/`0x5D`、消息 ID、子包、加密、长度、校验码），上行处理 车辆注册 / 实时定位 / 报警 三类报文，落内存表（可选 SQLite 持久化，不为一条协议引入 ORM）。
2. 对外：`getVehicles` / `getTrack` 查询 API，并与媒体流按「设备号/车牌」弱关联，供管理页面把「车」和「流」放在同一视图里看。
3. 设计要点：`src/ZlmSharp/Protocols/Jt809/`，报文编解码纯函数化（便于与标准样例对拍）；`Jt809Server`（`IHostedService`）+ 会话表管理连接与心跳；与媒体链路完全解耦，异常不影响推流。

**验收**：国标用 PS/RTP 模拟器（或抓包回放）跑通「注册 → INVITE → 收流 → 可播放」最小闭环；JT809 用构造报文单测 + 标准样例对拍（头/校验/分包/粘包边界）。

**风险**：两套规范版本差异大（国标 2016 vs 2022、JT809 2011 vs 2019），首期只做最小闭环并明确「非全量合规」；国标 UDP 收流对 NAT/端口池要求高，建议同时支持 TCP 被动/主动模式各一条路径再扩展。
