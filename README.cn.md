# zlmsharp

<img src="assets/app-icon.png" width="96" align="right" alt="zlmsharp" />

用 C#/.NET 10 复刻 [ZLMediaKit](https://github.com/ZLMediaKit/ZLMediaKit) 的流媒体核心：推流接入、媒体源分发、协议转换，以及一套与 ZLM 兼容的 HTTP API —— 一个单文件可执行程序 + 一个类库。

[English](README.md) | **简体中文**

- RTMP 推/播（HEVC 走 Enhanced RTMP 的 `hvc1` 扩展头）、RTSP 推/播（TCP interleaved + UDP）、HTTP-FLV、WebSocket-FLV；视频 H.264 / H.265
- 每路流一个 `MediaSource`：环形缓冲、GOP 快照、按读者分发并统计各读者丢帧
- MP4 / FLV / HLS(TS) 录制、RTMP 转推、RTSP/RTMP 拉流代理
- hook（`on_publish` / `on_play` / `on_stream_none_reader`）、会话管理、无人观看回收
- 可选 YOLO 检测 / 叠加（纯托管推理，无原生依赖，可跑在任意在线流上）
- 面向 NativeAOT 发布，配置绑定不依赖反射

---

## 快速开始

需要 .NET 10 SDK（`global.json` 钉住 `10.0.x`）。

```bash
git clone <repo> zlmsharp && cd zlmsharp
dotnet build zlmsharp.sln
dotnet run --project src/ZlmSharp.Cli
```

首次启动会在 exe 同目录（`src/ZlmSharp.Cli/bin/Debug/net10.0/`）生成 `zlmsharp.ini` 与 `.env`，然后监听：

| 服务 | 默认端口 | 说明 |
| --- | --- | --- |
| HTTP（API + HTTP-FLV） | 8080 | Windows 非管理员无法绑 80；要与 ZLM 完全一致就改 `[http] port` |
| RTSP | 554 | |
| RTMP | 1935 | |

命令行参数：`-c/--conf <ini>`、`-e/--env <file>`、`-v/--version`、`-h/--help`、`-d/--daemon`（为兼容 ZLM 保留，实际仍前台运行）。

### 推流与播放

```bash
# 推流（RTMP / RTSP）
ffmpeg -re -i input.mp4 -c copy -f flv rtmp://127.0.0.1:1935/live/test
ffmpeg -re -i input.mp4 -c copy -f rtsp -rtsp_transport tcp rtsp://127.0.0.1:554/live/test

# 播放
ffplay rtmp://127.0.0.1:1935/live/test
ffplay -rtsp_transport tcp rtsp://127.0.0.1:554/live/test
ffplay http://127.0.0.1:8080/live/test.live.flv      # HTTP-FLV
# WebSocket-FLV：同一路径，把请求升级成 WebSocket 即可
```

HTTP-FLV / WS-FLV / RTSP 的路径支持 `[vhost/]app/stream`（vhost 段可省略）；RTMP 在 `connect` 里只带 `app/stream`，流标识默认落在 `__defaultVhost__`。

## 配置

两个文件都可选，首次启动自动生成：

- `zlmsharp.ini`：业务配置，段名/键名与 ZLM `conf/config.ini` 对齐（`[api]`、`[general]`、`[http]`、`[rtsp]`、`[rtmp]`、`[protocol]`、`[hook]`、`[hls]`），另有 zlmsharp 扩展段 `[proxy]`（拉流代理）与 `[log]`（NLog）。
- `.env`：机器相关/敏感项：ffmpeg 路径、录像根目录、YOLO 模型目录等；进程环境变量优先于 `.env`。

ini 解析支持 `[section]`、`key=value`、整行 `;` / `#` 注释、同名键后者生效；用户文件是叠加在内置默认值之上的，升级新增的键不会缺省。取值越界会被钳制并给出启动警告，无法识别的键直接忽略。

## HTTP API

返回体统一是 ZLM 风格的 `{"code":0,"msg":"success","data":...}`。`GET /index/api/getApiList` 返回已实现的接口。非回环地址调用必须带 `secret=`（与 `[api] secret` 一致，清空该键则关闭校验）；来自 `127.0.0.1` / `::1` 的请求与 ZLM 一样免鉴权。

| 接口 | 作用 |
| --- | --- |
| `version`、`getApiList` | 构建信息、接口清单 |
| `getMediaList` | 在线流：编码、分辨率、帧率、读者数、存活时长、`originType`、`dropFrames`；可按 `vhost`/`app`/`stream`/`schema` 过滤 |
| `close_streams` | 关掉匹配的流，返回 `count` / `closed` |
| `getSessionList`、`kick_session` | 查看/踢掉推流与播放会话（`id=` 精确踢，或按条件批量） |
| `startRecord`、`stopRecord`、`isRecording` | 录像控制：`type=1` MP4、`2` FLV、`3` MP4+FLV、`4` HLS、`5` 全部 |
| `getMp4RecordFile`、`deleteRecordFile` | 查询/删除录像文件（`app`+`stream`+`period`+`path`），越界路径拒绝 |
| `startPush`、`stopPush`、`getPushList` | RTMP 转推 |
| `addStreamProxy`、`delStreamProxy` | 拉流代理（RTMP/RTSP 源 → 本地流） |
| `yoloInfo`、`yoloDetect`、`getYoloResult` | 模型信息、单次检测、定时检测的最近结果 |
| `startYoloOverlay`、`stopYoloOverlay`、`getYoloOverlay` | 把检测框画进派生流 `{stream}_yolo` |

## 拉流代理

```bash
# 把远端流拉到本地 live/tencent
curl "http://127.0.0.1:8080/index/api/addStreamProxy?app=live&stream=tencent&url=rtmp%3A%2F%2Fexample.com%2Flive%2Fdemo"
curl "http://127.0.0.1:8080/index/api/delStreamProxy?key=__defaultVhost__%2Flive%2Ftencent"
```

- 源支持 `rtmp://` 与 `rtsp://`（RTSP 走 TCP interleaved；`rtsp://user:pass@host/...` 会带 `Authorization: Basic`）。
- 本地流标 `originType=proxy`，`schema` 跟随源协议。
- 断线自动重连，直到 `delStreamProxy` / 进程退出；代理流与普通本地流一样可播放、录像、转推、YOLO 分析。
- 启动即拉清单：

```ini
[proxy]
enable=1
max_concurrency=16   # 超过这个路数的新拉流会被拒绝
retry_delay_ms=2000
list=live/cam1=rtsp://192.168.1.10:554/stream1; live/cam2=rtmp://example.com/live/demo
```

## 录制

- `startRecord?app=&stream=&type=&max_second=` 对在线流开始录制；文件落在 `ZLM_RECORD_ROOT`（回退 `protocol.mp4_save_path`）下的 `app/stream/yyyy-MM-dd/`。
- MP4 走 AVCC（`avcC` + `esds`），FLV 是追加式（崩溃可播），HLS 按关键帧对齐产出 `hls-000001.ts…` + `hls.m3u8`（`[hls] seg_duration`、`seg_num`）。
- 推流端断线重推后会自动续录；`protocol.enable_mp4` / `enable_flv_record` 打开时开机自动录（跳过 `*_yolo` 流）。

## YOLO（可选）

全部通过 `.env` 配置：`ZLM_YOLO_MODEL_DIR`、`ZLM_YOLO_MODEL`、`ZLM_YOLO_DEVICE`（`cpu`/`vulkan`/`metal`/`auto`）、`ZLM_YOLO_CONFIDENCE`、输入尺寸、线程数、采样间隔。模型懒加载并按 LRU 缓存；推理输入来自常驻 ffmpeg 解码器的最新一帧，结果按流保存并带稳定的 IoU `trackId`。`startYoloOverlay` 会拉起「解码 → 绘框 → 编码」管道，把 `{stream}_yolo` 推回本服务。

## Hook 与稳定性

```ini
[hook]
enable=1
on_publish=http://127.0.0.1:9000/on_publish
on_play=http://127.0.0.1:9000/on_play
on_stream_none_reader=http://127.0.0.1:9000/on_none_reader
timeoutSec=10
fail_open=0     # 0 = hook 不可达时拒绝，避免鉴权被绕过
```

hook 请求体字段与 ZLM 一致（`mediaServerId`、`app`、`stream`、`vhost`、`schema`、`id`、`ip`、`port`、`user_agent`、`params`）；推/播 URL 上的查询参数（如 `?token=xxx`）原样放进 `params`，由外部服务判定。无人观看的流在 `general.streamNoneReaderDelayMS` 之后按 `protocol.auto_close` / `on_stream_none_reader` 的规则回收。

## 嵌入式使用

```csharp
builder.Services.AddZlmSharp(o => o.Options.Http.Port = 8080);
var app = builder.Build();
app.UseZlmSharp();
```

`samples/ZlmSharp.Sample.Web` 演示了在普通 ASP.NET Core 应用里嵌入（默认不开监听端口，HTTP API 在 5180）。

## 仓库结构

```
src/ZlmSharp/           类库（程序集 ZlmSharp.MediaServer，命名空间与 ZLM 结构对齐）
  Core/ Codecs/ Configuration/ Protocols/{Flv,Rtmp,Rtsp}/ Http/ Recording/ Ai/ Api/ Server/
src/ZlmSharp.Cli/       可执行入口（产物 zlmsharp.exe，NativeAOT）
samples/…Sample.Web/    嵌入示例
tests/ZlmSharp.Tests/   xUnit 单元 + 端到端测试
assets/                 app.ico（多尺寸）与 app-icon.png，由 tools/make-icon.ps1 生成
```

## 构建、发布、测试

```bash
dotnet build zlmsharp.sln                       # 警告即错误，AOT 分析器全开
dotnet test  tests/ZlmSharp.Tests               # 268 个用例
dotnet publish src/ZlmSharp.Cli -c Release -r win-x64   # NativeAOT 单文件
```

NativeAOT 在 Windows 上需要 MSVC + Windows SDK（Linux 上需要 clang + zlib1g-dev）。发布目录里是一个裁剪后的 exe 与首次运行生成的 `zlmsharp.ini`，Win32 图标来自 `assets/app.ico`。

真实模型的 YOLO 冒烟测试（不进常规用例）：

```bash
ZLM_YOLO_TEST_MODEL=<path-to.onnx> dotnet test --filter YoloModelSmokeTests
```

## 已知限制

- 视频 H.264 / H.265，音频仅 AAC。HEVC 的 FLV 头有 legacy（`codecId=12`）与 Enhanced RTMP 两种写法，兼容性细节见下文「Enhanced RTMP 兼容性」。
- RTSP 拉流代理只走 TCP interleaved；RTSP 推流侧会回 RR，但从不发 SR。
- HLS 目前只是录制格式，`protocol.enable_hls` 仅完成配置绑定，未接到播放侧。
- MP4 需要停止录制后才可播放（moov 在收尾时写），要对崩溃宽容请用 FLV。
- 单进程、状态在内存：无集群，无 RTSP/RTMP over SSL，无 WebRTC。

## Enhanced RTMP 兼容性（HEVC 的两种 FLV 头）

HEVC 放进 FLV/RTMP 时存在两套互不兼容的写法，本服务**两边都处理**：接收两种，发送可切换。

### 两种写法的差异

| 项 | legacy（非标准，野外常见） | Enhanced RTMP（E-RTMP v1/v2） |
|---|---|---|
| 视频 tag 首字节 | `[FrameType:4][CodecId:4]`，HEVC 固定 `codecId=12`：关键帧 `0x1C`、普通帧 `0x2C` | `0x80 \| [FrameType:3][PacketType:4]`：序列头 `0x90`、关键帧 `0x93`、普通帧 `0xA1` |
| 编解码器标识 | 首字节低 4 位 = 12 | 首字节后 4 字节 FourCC `'hvc1'` |
| 配置帧 | 第 2 字节 `AVCPacketType=0` + `hvcC` | `PacketType=0`（SequenceStart）+ `hvcC` |
| 编码帧 | 第 2 字节 `AVCPacketType=1` + 固定 3 字节 CTS + AVCC 负载 | `PacketType=1`（CodedFrames）带 3 字节 CTS；`PacketType=3`（CodedFramesX）省略 CTS，视为 0 |
| `onMetaData.videocodecid` | `12` | `1752589105`（`'hvc1'` 的整数值，与 FFmpeg 8.x / OBS 实测一致） |

### 接收（推流、拉流代理）

两种格式**自动识别、无需配置**：按视频 tag 首字节 bit7 判定，逐 tag 解析，同一进程里可以同时有 legacy 与增强的推流端。

- bit7 = 0 且低 4 位 = 12：按 legacy 解析（第 2 字节当 `AVCPacketType`，第 3~5 字节当 CTS）
- bit7 = 1：按增强解析（读 FourCC 与 `PacketType`，`CodedFramesX` 的 CTS 记 0）
- `PacketType=2/4/6/7`（SequenceEnd / Metadata / Multitrack / ModEx）：忽略，不当作图像帧
- H.264（低 4 位 = 7）保持不变，仍走 legacy

### 发送（播放、转推、HTTP-FLV/WS-FLV）

一个 FLV tag 只能有一种头，所以输出是**全局二选一**，配置在 `zlmsharp.ini`：

```ini
[rtmp]
; 1 = HEVC 用增强头（默认）；0 = 退回 legacy codecId=12
enhanced=1
```

- 同一开关同时作用于 RTMP 播放、RTMP 转推、HTTP-FLV、WS-FLV；只影响新建立的连接（已连接的流不重协商）。
- 两种模式下 **H.264 都保持 legacy**（`0x17` / `0x27`）：规范里 `avc1` 增强头只用于多轨场景，legacy 兼容性最好。
- `onMetaData.videocodecid` 跟着变：增强模式写 `1752589105`，legacy 模式写 `12`。
- FLV 录像**固定写 legacy**，保证老播放器与 ffmpeg 工具链都能读文件。

### 对端支持情况

| 对端 | 收增强头 | 收 legacy 12 | 说明 |
|---|---|---|---|
| FFmpeg 8.x | ✅ | ✅ | 本机 8.1.2 实测双向 |
| FFmpeg 7.1+ | ✅ | ✅ | 增强头支持从 7.1 开始 |
| FFmpeg ≤ 7.0 | ❌ | ✅ | 只认 legacy，需要 `enhanced=0` |
| 浏览器 `mpegts.js`（管理页面预览） | ❌ | ❌ | 不支持 FLV 里的 HEVC，与头格式无关；看 HEVC 请用 RTSP 或支持 `hvc1` 的播放器 |

OBS 30+ 推 HEVC/AV1 时默认就发增强头，本服务可直接接收，不需要额外配置。

### 实测记录

本机 FFmpeg 8.1.2（`ZLM_FFMPEG_TEST_PATH` 指向该 exe）：

1. `ffmpeg -c:v libx265 -f flv` 推流 → 本服务解析出 H265 320x240（增强头入站）；
2. ffprobe 从本服务 RTMP 拉流：`enhanced=1` 与 `enhanced=0` 两种输出都识别为 `hevc`、宽 320；
3. 与本机 ffmpeg 生成的增强 FLV 对拍：视频 tag 首字节同样是 `0x90` + `'hvc1'`，`videocodecid` 同样是 `1752589105`，与本服务输出一致。

### 仍未支持

- `PacketType=6`（Multitrack）、`7`（ModEx / 纳秒时间戳）只识别不用；
- AV1 / VP9 的 FourCC（`av01` / `vp09`）能识别但不解码；
- 无法按客户端分别选头：目前只能全局切换，若需要 `?enhanced=0` 这类按请求覆盖再另行实现。

## 路线图

进度与详细设计见 `workplan.md`（已被 `.gitignore` 排除，不入库）。下一阶段：

1. **H.265/HEVC 全链路**：VPS/SPS/PPS 解析、RFC 7798 RTP、FLV `hvcC`、MP4 `hvc1`、TS `0x24`、RTSP/RTMP 推流播放与转推。
2. **管理页面**：内置静态站点（前端零构建），复用现有 `/index/api/*`，覆盖流/会话/录像/代理转推/AI 结果。
3. **AI 通用层**：拆成取帧 / `IStreamAnalyzer` / `IOverlayRenderer` 三层，YOLO 变成一个插件，后续可加 OCR、车牌、区域布防等分析器。
4. **国标 GB/T 28181 与 JT/T 809**：SIP + PS over RTP 摄像头接入，以及车辆注册/定位/报警报文接入。

## 许可证

GPL-3.0，见 [LICENSE](LICENSE)。
