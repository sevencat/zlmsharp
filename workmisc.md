# 杂项与踩坑（zlmsharp）

配套文档：`workplan.md`（里程碑与范围）。本文件只放"不属于某个功能点"的杂事，以及实现过程中踩到的坑；新增条目按分类追加，不要删历史条目（标记"已解决/规避"即可）。

## A. 杂项

### A1. 环境
- OS：Windows（开发）；目标跨平台（Linux 部署）。
- SDK：.NET 10（本机 `10.0.101`，另有 `9.0.319`）；用 `global.json` 钉住 `10.0.x`，避免队友用错 SDK。
- **AOT 前置**：Windows 需 Visual Studio Build Tools 的"使用 C++ 的桌面开发"（MSVC + Windows SDK）；Linux 需 `clang` + `zlib1g-dev`。缺少时 `PublishAot` 直接报链接器错误，与代码无关。
- 仓库：`work/` 放 ZLM 源码（已 gitignore），`workplan.md`/`workmisc.md` 也不入库，需要时自行备份。
- YOLO 模型仓库（本机）：`F:\ai\models\yolo`（30 个 ONNX：COCO 通用检测 + 人/车/火/河道/车牌等专用 + seg/pose/obb/cls）。`.env` 用 `ZLM_YOLO_MODEL_DIR` 指向它、`ZLM_YOLO_MODEL` 只写文件名（默认 `yolov8s.onnx`）。
- 端口约定（与 ZLM 默认对齐）：HTTP 80（本机开发用 8080，避免管理员权限）、RTSP 554（开发用 8554）、RTMP 1935、RTP 收流 10000（一期不开）。
- 配置文件：**`zlmsharp.ini`**（业务配置）+ **`.env`**（机器相关/敏感/工具路径），缺省都在 exe 同目录（`AppContext.BaseDirectory`）；开发时即 `src/ZlmSharp.Cli/bin/<Configuration>/net10.0/`。不要命名为 `config.ini`，也不要读 `appsettings.json`。
  - 首次启动自动生成两者（默认值唯一来源：`src/ZlmSharp/Configuration/DefaultConfig.cs` 与 `DefaultEnv.cs`，不维护第二份 sample 文件）。
  - 生成的默认 ini 中 `http.port=8080`（ZLM 为 80），因为 Windows 非管理员无法绑定 80；文件注释里已注明，部署时可改回 80。
  - `.env` 里放 `ZLM_FFMPEG_BIN`、`ZLM_FFPROBE_BIN`、`ZLM_RECORD_ROOT`、`ZLM_YOLO_MODEL`、`ZLM_YOLO_ENDPOINT` 等；进程环境变量优先于 `.env`。
- 嵌入模式：任意 ASP.NET Core 应用 `builder.Services.AddZlmSharp(...)` + `app.UseZlmSharp()`；示例 `samples/ZlmSharp.Sample.Web`（端口 5180，监听服务默认关闭）。

### A2. 依赖与工具
- 运行时依赖：**NLog + NLog.Extensions.Logging**（日志），其余为 BCL/官方包；xUnit 仅测试工程引用。
- YOLO 推理：**SimdYolo 0.11.2**（纯托管 C#/.NET 10，CPU SIMD + Vulkan/Metal，无 Python/ORT/CUDA 依赖）。它**没上架 nuget.org**，只有 GitHub Release 的 `.nupkg`：文件放 `packages/`，仓库根 `nuget.config` 把它登记成本地源（`zlmsharp-local`）；升级版本时替换该 `.nupkg` 并同步 csproj 里的 Version。
- ffmpeg（本机）：`E:\program\ffmpeg\81\ffmpeg.exe`。注意 `81\20260402\` 目录里只有 `avcodec-62.dll` 等运行库，**没有 exe**；扫盘时容易看错。
  - 代码里一律通过 `EnvOptions.ResolveFfmpegPath()`（`.env` 的 `ZLM_FFMPEG_BIN`）取路径，不得硬编码；留空则用 PATH。
- 调试工具：`ffmpeg`/`ffprobe`/`ffplay`、VLC、Wireshark（过滤 `rtsp || rtmp || rtp`）、`dotnet-counters`、`dotnet-trace`。AOT 产物下 `dotnet-counters` 不可用，性能问题先用普通发布复现。
- ZLM 自带资源可参考：`work/ZLMediaKit-master/www/`（测试播放页）、`postman/`（API 集合）、`conf/config.ini`（**键名与默认值的参照物**，仅参考，本项目文件叫 `zlmsharp.ini`）。

### A3. 常用命令（PowerShell）
```powershell
$ffmpeg = 'E:\program\ffmpeg\81\ffmpeg.exe'   # 或写进 .env 的 ZLM_FFMPEG_BIN
# 构建/运行（默认读 exe 同目录 zlmsharp.ini/.env，可 -c / -e 覆盖）
dotnet build; dotnet run --project src/ZlmSharp.Cli -- -c .\zlmsharp.ini -e .\.env
# 单测
dotnet test tests/ZlmSharp.Tests
# AOT 发布（Windows；Linux 用 -r linux-x64）
dotnet publish src/ZlmSharp.Cli -c Release -r win-x64 -p:PublishAot=true -o dist/win-x64
# 嵌入模式示例（另一个普通 web 应用）
dotnet run --project samples/ZlmSharp.Sample.Web
# 生成 H264 测试素材（Annex-B）
& $ffmpeg -f lavfi -i testsrc=size=1280x720:rate=25 -t 10 -c:v libx264 -g 50 -bf 0 -f h264 test.h264
# 推 RTMP，同时用 HTTP-FLV 拉
& $ffmpeg -re -i test.h264 -c copy -f flv rtmp://127.0.0.1:1935/live/test
& 'E:\program\ffmpeg\81\ffplay.exe' -i http://127.0.0.1:8080/live/test.live.flv
# 推 RTSP
& $ffmpeg -re -i test.h264 -c copy -rtsp_transport tcp -f rtsp rtsp://127.0.0.1:8554/live/test
# 抓包
& 'C:\Program Files\Wireshark\dumpcap.exe' -i 1 -f "tcp port 1935 or tcp port 8554" -w rtmp_rtsp.pcapng
# YOLO：下一个 COCO 模型，.env 里 ZLM_YOLO_MODEL=./yolov8n.pt
Invoke-WebRequest "https://github.com/ultralytics/assets/releases/download/v8.3.0/yolov8n.pt" -OutFile .\yolov8n.pt
# 抽一帧 → 传 PPM 检测（先让 8554 上有流）
& $ffmpeg -i rtsp://127.0.0.1:8554/live/test -frames:v 1 -f image2pipe -vcodec ppm frame.ppm
curl.exe -X POST --data-binary "@frame.ppm" http://127.0.0.1:8080/index/api/yoloDetect
# 或者让服务端自己用 ffmpeg 抽帧再检测
curl.exe -X POST "http://127.0.0.1:8080/index/api/yoloDetect?url=rtsp://127.0.0.1:8554/live/test"
curl.exe http://127.0.0.1:8080/index/api/yoloInfo
# 拉流代理：把远端 RTMP 拉到本地 live/tencent，然后本地直接播（ffplay/VLC）
curl.exe "http://127.0.0.1:8080/index/api/addStreamProxy?app=live&stream=tencent&url=rtmp%3A%2F%2Fliteavapp.qcloud.com%2Flive%2Fliteavdemoplayerstreamid"
& 'E:\program\ffmpeg\81\ffplay.exe' -i rtmp://127.0.0.1:1935/live/tencent
# 定时检测结果（每路流最近一次）
curl.exe "http://127.0.0.1:8080/index/api/getYoloResult?app=live&stream=tencent"
# 结果叠加到画面：输出 live/tencent_yolo（fps/bitrate/scale 可选）
curl.exe "http://127.0.0.1:8080/index/api/startYoloOverlay?app=live&stream=tencent"
curl.exe "http://127.0.0.1:8080/index/api/getYoloOverlay"
& 'E:\program\ffmpeg\81\ffplay.exe' -i rtmp://127.0.0.1:1935/live/tencent_yolo
# 只抓一帧看画框效果
& 'E:\program\ffmpeg\81\ffmpeg.exe' -y -i rtmp://127.0.0.1:1935/live/tencent_yolo -frames:v 1 overlay.png
curl.exe "http://127.0.0.1:8080/index/api/stopYoloOverlay?app=live&stream=tencent"
# 录制：type=1 MP4（默认）/2 FLV/3 两者；max_second 切片秒数（默认 protocol.mp4_max_second）
curl.exe "http://127.0.0.1:8080/index/api/startRecord?app=live&stream=tencent&type=3&max_second=600"
curl.exe "http://127.0.0.1:8080/index/api/isRecording?app=live&stream=tencent"
& 'E:\program\ffmpeg\81\ffprobe.exe' -v error -show_entries stream=codec_name,codec_type,width,height,sample_rate -of json "F:\...\www\live\tencent\2026-10-26\21-13-37-001.mp4"
curl.exe "http://127.0.0.1:8080/index/api/stopRecord?app=live&stream=tencent"
# 转推：把本地流推到另一个 RTMP（本机 1935 也能测，注意别把流名推成自己）
curl.exe "http://127.0.0.1:8080/index/api/startPush?app=live&stream=tencent&url=rtmp%3A%2F%2F127.0.0.1%3A1935%2Flive%2Ftencent_copy"
curl.exe "http://127.0.0.1:8080/index/api/getPushList"
& 'E:\program\ffmpeg\81\ffplay.exe' -i rtmp://127.0.0.1:1935/live/tencent_copy
curl.exe "http://127.0.0.1:8080/index/api/stopPush?app=live&stream=tencent"
# 停止代理（key 就是 vhost/app/stream，中间要转义）
curl.exe "http://127.0.0.1:8080/index/api/delStreamProxy?key=__defaultVhost__%2Flive%2Ftencent"
```
- H.264 测试素材统一 `-bf 0`（无 B 帧），一期不支持 B 帧重排；需要 B 帧再单独评估。

### A4. 代码约定
- `nullable` 开启，`warnings as errors`；公共 API 必须有 XML 注释（至少媒体核心层）。
- 命名对齐 ZLM 类名（见 `workplan.md` §2 映射表），便于对照源码 review。
- 二进制协议代码禁止用 `string` 中转（除 RTSP/SDP 这类文本协议）；统一 `ReadOnlySequence<byte>` → 自行 `Span` 解析。
- 所有 `TODO` 带 `// TODO(zlm): ...` 前缀，方便全局搜索清理。
- 任何新代码先过 `B10 NativeAOT`：不引入反射/动态/`Enum.Parse`，JSON 走源生成；不确定就先写最小 AOT publish 验证。
- 提交粒度 = 一个里程碑子任务；提交信息 `feat(core): ...` / `fix(rtmp): ...`。

### A5. 参考资料（读源码的顺序）
1. `src/Common/MediaSource.cpp`、`MediaSink.cpp` → 媒体模型
2. `src/Extension/Frame.h`、`Track.h` → 帧/轨道字段语义
3. `src/Rtmp/FlvMuxer.cpp` → FLV 封装（HTTP-FLV/WS-FLV/RTMP 共用）
4. `src/Rtsp/RtspSession.cpp` + `src/Rtsp/Rtsp.cpp` → SDP/RTP 打包
5. `src/Rtmp/RtmpProtocol.cpp` + `RtmpSession.cpp` → 握手/Chunk/AMF
6. `server/WebApi.cpp` → API 字段与参数（一手的返回体样例）
7. `conf/config.ini` → 配置键与默认值

### A6. 端到端跑通腾讯测试流（代理 → 定时检测 → 叠加）

本机已验证的最短路径；前提是 `src/ZlmSharp.Cli/bin/Debug/net10.0/.env` 里已配好 `ZLM_FFMPEG_BIN=E:\program\ffmpeg\81\ffmpeg.exe`、`ZLM_YOLO_MODEL_DIR=F:\ai\models\yolo`、`ZLM_YOLO_MODEL=yolov8s.onnx`（首次启动会自动生成模板）。

**1. 启动服务（窗口 A，保持运行）**

```powershell
cd f:\code\zlmsharp\src\ZlmSharp.Cli\bin\Debug\net10.0
.\zlmsharp.exe
```

端口：HTTP 8080、RTSP 554、RTMP 1935（`zlmsharp.ini` 可改）。日志在 exe 同目录 `log/zlmsharp.log`。

**2. 把腾讯 demo 流拉到本地 `live/tencent`（窗口 B）**

```powershell
curl.exe "http://127.0.0.1:8080/index/api/addStreamProxy?app=live&stream=tencent&url=rtmp%3A%2F%2Fliteavapp.qcloud.com%2Flive%2Fliteavdemoplayerstreamid"
```

- 返回 `{"code":0,"data":{"key":"__defaultVhost__/live/tencent"}}` 表示拉流任务已启动（断线会自动重连，2s 间隔）。
- `url=` 里的 `://` 必须转义成 `%3A%2F%2F`，否则 `&` 后的参数会被截断；代理只接受 `rtmp://` 源。
- 网络不通时这一步 `code=0` 也可能注册成功但拉不到流，下一步 `getMediaList` 会是空。

**3. 确认流在线**

```powershell
curl.exe "http://127.0.0.1:8080/index/api/getMediaList"
```

期望看到 `stream=tencent`、`videoCodec=H264`、`videoWidth=1280`、`videoHeight=720`。`videoFps=0` 属正常（还没统计帧率）；`readerCount` 至少 1，其中可能包含定时检测的抽帧连接。

**4. 等 3~5s 看定时检测结果**

```powershell
curl.exe "http://127.0.0.1:8080/index/api/getYoloResult?app=live&stream=tencent"
```

期望 `objects` 非空、`ageMs < 1000`、`error=null`，每个框带跨帧稳定的 `trackId`（同一目标持续出现时不变）。首次采样包含模型加载（~0.3-0.6s），可能慢一拍；`objects` 为空时先看 `error`。

**5. 打开叠加流**

```powershell
curl.exe "http://127.0.0.1:8080/index/api/startYoloOverlay?app=live&stream=tencent"
curl.exe "http://127.0.0.1:8080/index/api/getYoloOverlay"
curl.exe "http://127.0.0.1:8080/index/api/getMediaList"
```

期望：`state=running`；隔几秒 `frames` 约按 25/s 增长；`getMediaList` 多出 `tencent_yolo`（`videoCodec=H264`、`audioCodec=unknown`，叠加流只有视频）。叠加流固定命名 `{stream}_yolo`，且定时检测会跳过它（防止无限套娃）。

**6. 播放 / 抓帧验证**

```powershell
# 直接播放（这个地址只在叠加流运行期间存在）
& 'E:\program\ffmpeg\81\ffplay.exe' -fflags nobuffer -i rtmp://127.0.0.1:1935/live/tencent_yolo

# 或用 VLC「打开网络串流」：rtmp://127.0.0.1:1935/live/tencent_yolo

# 只抓一帧看画框
& 'E:\program\ffmpeg\81\ffmpeg.exe' -y -i rtmp://127.0.0.1:1935/live/tencent_yolo -frames:v 1 overlay.png
start overlay.png

# 流参数检查
& 'E:\program\ffmpeg\81\ffprobe.exe' -v error -show_entries stream=codec_name,width,height,avg_frame_rate -of default=nw=1 rtmp://127.0.0.1:1935/live/tencent_yolo
```

**7. 收尾**

```powershell
curl.exe "http://127.0.0.1:8080/index/api/stopYoloOverlay?app=live&stream=tencent"   # data.flag=true
curl.exe "http://127.0.0.1:8080/index/api/delStreamProxy?key=__defaultVhost__%2Flive%2Ftencent"
```

停止后 `getYoloOverlay` 为空、`tencent_yolo` 从 `getMediaList` 消失；服务重启不会恢复叠加流，需要重新调 `startYoloOverlay`。

> 注意：PowerShell 里 `curl` 是 `Invoke-WebRequest` 的别名，参数语义不同，统一写 `curl.exe`。

## B. 踩坑清单

> 写法：现象 → 原因 → 处理。已踩到的补充准确报错信息与 commit/issue 链接。

### B1. 时间戳与时钟
- 用 `DateTime.Now` 做时间戳 → 系统时间跳变导致播放卡死/花屏。统一 `Stopwatch` 单调时钟，流内 `Pts/Dts` 从 0 递增（毫秒）。
- FLV/RTMP 时间戳是 32 位毫秒，会回绕（约 49.7 天）；RTSP RTP 是 90kHz、初始值随机。跨协议转换必须显式做基准差与回绕处理，不能直接透传。
- 音频/视频共用一条时间轴；只搬视频时也要给音轨预留（一期无音频可忽略，但接口要留）。

### B2. 内存与 GC
- 每帧 `byte[]` 拷贝 → LOH 爆炸、GC 停顿。用 `ArrayPool<byte>` + 引用计数缓冲，`MediaFrame` 只持 `ReadOnlyMemory<byte>`，读者 `Dispose` 后归还。
- 环形缓冲要设 `max_frame_ms` 上限，否则慢客户端 + 无上限 = 内存无界。GOP 缓存同样要按 keyframe 边界裁剪。
- `Channel<T>` 无界 = 隐患；reader 侧一律有界 + 丢弃策略（丢非关键帧，必要时断开慢客户端）。

### B3. 网络与拆包
- TCP 粘包/半包：一次 `ReceiveAsync` 可能拿到多帧或半帧。所有协议走"缓冲区 + 循环解析 + 剩余保留"的写法，禁止假设"一次收一个包"。
- RTSP 与 RTP over TCP 混流：`$`(0x24) 开头的 interleaved 帧与 RTSP 文本共用一条 TCP，必须按当前模式切换解析；半包同样要缓存。
- RTSP over HTTP 隧道（`POST` + base64）会被部分 ffmpeg/VLC 使用，若不做要能明确拒绝并给日志，否则表现为"偶发连不上"。
- `SO_REUSEADDR`/IPv6：Windows 上端口被 TIME_WAIT 占用、`localhost` 解析到 `::1` 都会表现为"端口监听成功但连不上"。
- 写失败（对端 RST）必须立刻清理会话；只靠 `Socket.Connected` 判断不可靠。

### B4. RTMP
- 握手：复杂握手（含 digest/HMAC-SHA256）是主流客户端默认行为，只有简单握手会握手失败（ffmpeg 会重试/降级，容易误判）。
- **反向坑（已踩）**：服务端走简单握手时，**S1 的 version 字段（S1 偏移 4~7）必须为 0**。我们之前 `Random.NextBytes(S1)` 忘了清这段，ffmpeg 看到 `S1[5] >= 3` 就按复杂握手校验 HMAC digest，直接报 `Server response validating failed`（现象：ffprobe/ffplay 打不开我们的 RTMP，自己的测试客户端却一切正常）。C1 的 version 同理写 0。
- Chunk：`csid` 扩展、`chunk size` 生效时机、type 3 后续 chunk 复用上个头部、扩展时间戳标记（0xFFFFFF）都要覆盖；`Set Chunk Size` 必须早于大包发送。
- AMF0 对象结束标记 `0x00 0x00 0x09`，漏写会让客户端一直等；AMF3 切换（`0x11`）只在 `connect` 前处理。
- 发布侧必须先回 `Window Ack Size` + `Set Peer Bandwidth` + `Set Chunk Size`，再 `onStatus(NetStream.Publish.Start)`，顺序错客户端会静默不推流。
- `@setDataFrame` 里带 `onMetaData`，别把 `@setDataFrame` 本身当事件名；`|RtmpSampleAccess` 要补。
- `deleteStream`/断连后必须移除发布者，否则再次 publish 会命中"stream 已存在"。

### B5. RTSP
- SDP 的 `sprop-parameter-sets` 必须带 SPS/PPS（base64），否则部分客户端（尤其 GStreamer）无法解码。
- 流中 SPS/PPS 变化要重新下发 config 帧并更新 SDP（重新 DESCRIBE 或带内下发）。
- FU-A 分包：`S`/`E` 位、`NRI` 传递、包长控制在 MTU 内（1200~1400）；STAP-A 只在同一时间戳的 SPS+PPS 组合时用。
- 未发 RTCP SR 时，部分客户端（VLC）会等不到同步源而黑屏/超时；SR 的 NTP 与 RTP 时间戳要成对换算。（已解决：`RtcpSenderReport`，首帧后立即发一次、之后每 5s；TCP/UDP 都发）
- `Session` 头缺失/超时：`GET_PARAMETER` 或 `OPTIONS` 保活，60s 无包断开。
- 一个 stream 有多个 SETUP（audio/video）时 track id 与 `interleaved=0-1/2-3` 的对应关系容易错位。

### B6. HTTP-FLV / Kestrel / WS
- 响应缓冲：必须 `DisableBuffering` + 手动 `FlushAsync`，否则 Kestrel 攒包导致"延迟几秒才出画面"。
- HTTP-FLV 首包顺序：`FLV header` → `onMetaData` → `AVC sequence header` → 最新 GOP 的关键帧起。顺序错播放器直接黑屏。
- 客户端断开只能靠写异常/`RequestAborted` 感知；要主动 `CancellationToken` 传播到 reader 注册，避免 reader 泄漏。
- WebSocket 需要 ping/pong 保活；不设发送超时，慢客户端会拖死整个会话；WS 分片发送时注意单帧不要过大（>64KB 考虑分片）。
- 不要用 `Response.ContentLength` 给直播流；用 chunked。
- Kestrel 与自研 RTSP/RTMP 监听端口分开配置；若将来要做"同端口协议嗅探"，需要换 Kestrel 为裸 TCP（先不做）。

### B7. 并发与生命周期
- C++ 版依赖单线程 `EventPoller`，天然无锁；.NET 里 `AddFrame` 会被多线程调用，必须明确：加锁范围、广播是"投递到 Channel"而非同步 await 所有 reader。
- 广播里 `await` 每个 reader = 一个慢客户端拖死整条流；一律 fire-and-forget 投递 + 有界队列。
- 会话/源的字典用 `ConcurrentDictionary` + `TryRemove` 幂等清理；关闭路径可能被多处触发（客户端断开、API 关流、超时），必须可重入。
- 不要用 `Task.Run` 包裹 socket 循环，直接 `async` 循环；同步阻塞（`.Result`/`.Wait()`）会导致线程池饥饿。

### B8. 配置与 API 兼容
- 配置文件叫 `zlmsharp.ini`（不叫 `config.ini`，避免和宿主机/其他组件的配置混淆）；键名与默认值照抄 ZLM `config.ini`，单位和大小写敏感（`streamNoneReaderDelayMS`、`maxStreamWaitMS` 等）。
- 路径必须是 `AppContext.BaseDirectory` + 文件名；用 `Directory.GetCurrentDirectory()` 会在服务/快捷方式启动时跑偏（工作目录不一定是 exe 目录）。
- `getMediaList` 的字段（`app/stream/schema/vhost/readerCount/totalReaderCount/originType/originUrl/createStamp/aliveSecond/bytesSpeed/tracks`）少一个就可能让前端/脚本报错；用 ZLM 实际返回做字段快照比对。
- `setServerConfig` 只改内存配置还是落盘，需要明确（ZLM 仅内存生效）。
- API 的 `code` 非 0 也是 200 响应体，不要用 HTTP 状态码表达业务错误。

### B9. 调试经验
- 协议问题优先"抓包 + 十六进制 dump"，不要靠日志猜；RTSP 文本务必用 `\r\n`，用错 `\n` 会被直接丢弃。
- 日志里打印二进制只打前 32 字节十六进制 + 长度。
- 播放器行为差异大：先用 ffplay 定位（报错最直接），再验 VLC/浏览器。

### B10. NativeAOT
- `Directory.Build.props` 里加 `IsAotCompatible=true`/`EnableTrimAnalyzer=true`，**每个 PR 都跑一次 Release publish**；等到最后再开 AOT 会集中爆一堆 `IL2xxx`，改造成本极高。
- JSON：反射序列化在 AOT 下直接抛 `InvalidOperationException`（或裁剪后返回空对象）。所有返回体走 `[JsonSerializable]` 上下文，`getServerConfig` 这类动态字典用 `Dictionary<string,string>` 显式序列化。
- 配置绑定：`IConfiguration.Bind/Get<T>` 依赖反射，AOT 下不可靠。手写 `IniDocument` + 显式 `Bind`/`KeyMap`，顺带解决"getServerConfig 要保留字符串原文"的需求。
- `Enum.Parse`/`TypeConverter`/`Activator`：AOT 下可能被裁掉，统一显式 switch/工厂方法。
- `RegexOptions.Compiled` 在 AOT 下无意义且告警，改 `[GeneratedRegex]`。
- `Assembly.Location` 在单文件/AOT 下为空字符串，配置与静态资源路径一律用 `AppContext.BaseDirectory`。
- `Encoding.GetEncoding("GB18030")` 需要 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`，且该包要显式引用；AOT 下比 JIT 下更容易静默失败，启动时打一条自检日志。
- 日志/指标：AOT 下 `dotnet-counters`/`dotnet-trace` 不可用（无运行时诊断端口），性能剖析用普通发布或内置统计接口。
- 发布体积/启动：ASP.NET Core 基础版 AOT 约 15~25MB；`InvariantGlobalization=true` 能减小体积但会破坏文化相关格式化与部分编码转换，**先不开**。
- Kestrel + `WebApplication.CreateSlimBuilder` 是官方 AOT 通道；`CreateDefaultBuilder` 会拉 JSON 配置/反射式日志配置，别用。`AddControllers`（MVC）在 AOT 下不支持，API 一律用 `MapGet/MapPost` 显式委托。
- 交叉发布：`win-x64` 与 `linux-x64` 各自在自己的 OS 上发布最稳（跨平台链接可能缺工具链）；CI 用两个 job。
- 发布目录残留：ILC 会产出原生 pdb，`StripSymbols=true` 只处理 exe 内符号；要 `CopyOutputSymbolsToPublishDirectory=false`，再配合 `AfterTargets="Publish"` 的 `Delete` 兜底（`$(PublishDir)` 是相对路径，Include 里必须先 `GetFullPath`）。
- 程序入口用 `Microsoft.NET.Sdk` + `FrameworkReference Microsoft.AspNetCore.App`，别用 `Microsoft.NET.Sdk.Web`：后者会带出 `staticwebassets.endpoints.json`、`aspnetcorev2_inprocess.dll`（自托管用不到）。
- 实测（win-x64, .NET 10.0.101）：AOT exe 约 9.3MB，冷启动到 HTTP 可用约 300ms；`dotnet run` 仍走 JIT，调试不受影响。
- 构建信息：`AssemblyMetadata` 由 MSBuild 注入（`BuildTime`/`BranchName`/`CommitHash`），本地构建 `SourceRevisionId` 为空 → `commitHash=unknown` 属正常；CI 传 `-p:SourceRevisionId=<git short sha>` 即可。
- 已验证（M0）：`CreateSlimBuilder` + Kestrel + 源生成 JSON + **NLog** 在 AOT 下 0 警告、控制台/文件目标均正常。
- 控制台重定向到文件时是 UTF-8，PowerShell 5.1 的 `Get-Content` 默认按 ANSI 读会乱码，看日志加 `-Encoding utf8`。

### B11. NLog
- NLog 大量依赖反射（layout renderer、target 工厂、配置 XML）。**不用 nlog.config**，全部用 `new LoggingConfiguration()` + `new FileTarget/ColoredConsoleTarget` 程序化配置。
- AOT 必须整体保留程序集：CLI csproj 加
  `<TrimmerRootAssembly Include="NLog" />`、`<TrimmerRootAssembly Include="NLog.Extensions.Logging" />`；只靠分析器 0 warning 不代表运行期一定 OK，必须跑一次 AOT 冒烟。
- NLog 6 API 变化：`FileTarget.ConcurrentWrites` 已移除；`ArchiveNumbering` 过时（编译错误因 TreatWarningsAsErrors），改用 `ArchiveSuffixFormat = ".{#}"`。
- 规则过滤的坑：NLog 规则**级别不在区间内就继续匹配下一条**，所以"给 Microsoft.* 加一条 Warn..Fatal 规则"不能压掉 Info 日志；要压掉低级别，必须加一条 **Trace..Info + NullTarget + `Final=true`** 的规则，放在通用规则之前。
- MEL 过滤器（`logging.AddFilter`）对 NLog provider 的可见效果实测不可靠（我们踩过：加了 filter 仍输出 Info），优先用 NLog 自身规则表达过滤。
- `[log] level>=2` 时框架日志（`Microsoft.*`）被吞到只剩 Warn，别再指望 `Microsoft.Hosting.Lifetime` 的 "Now listening" 日志，zlm 自己的启动日志已包含端口信息。
- 文件路径支持相对路径（相对 exe 目录），日志目录不存在会自动创建（`CreateDirs=true`）。

### B12. 宿主库化 / 嵌入
- 端点挂载只依赖 `IEndpointRouteBuilder`：`app.UseZlmSharp()`（`WebApplication` 实现该接口）或 `endpoints.MapZlmSharp()`（`UseEndpoints` 内）。不要在库里 `new WebApplication`，否则嵌入模式会抢宿主端口。
- 配置加载放在 `AddZlmSharp` 时同步完成（`ZlmSharpRuntime.Create`），这样 NLog 配置、监听端口在容器构建前后都可预期；`ZlmSharpRuntime` 注册为单例，`AppOptions`/`IniDocument`/`DotEnvDocument` 直接注入。
- 嵌入普通 web 应用时：`ConfigureLogging=false` 用宿主日志、`RegisterStreamListeners=false` 由宿主决定是否开 RTSP/RTMP 端口；否则 NLog 与控制台 provider 会重复输出。
- 独立模式（CLI）必须 `ClearOtherLoggingProviders=true`，否则默认 Console provider 与 NLog 控制台目标双写。
- 库不读 `appsettings.json`：`builder.Configuration.Sources.Clear()` 只在 CLI 里做；嵌入时不要清宿主的配置源，否则宿主自己的配置全丢。

### B13. H.264 / Annex-B
- 同一位置必须**优先识别 4 字节起始码**：`00 00 00 01` 若先按 3 字节匹配（从第二个 0 开始），会把起始码前导 0 算进上一个 NALU，表现为"每帧多 1 字节、偶发花屏"。
- 反过来，NALU 数据本身可能以 `0x00` 结尾（rbsp/cabac zero words），不能无条件裁掉末尾 0；只在"确实属于下一个 4 字节起始码"时裁。
- 增量拆分器在"起始码正好落在缓冲区末尾"时必须保留 pending 状态（`pending=0, length=0`），否则下一包的 NALU 内容会被当垃圾丢掉；逐字节喂数据是最容易暴露该 bug 的测法。
- C# 限制：`ReadOnlySpan<byte>` 不能作为 `ValueTuple` 泛型参数（CS9244），拆包 API 用 `out ReadOnlySpan<byte>` + 返回 consumed，别写 `(nal, consumed)` 元组。
- SPS 里 `profile_idc` 属于 High 系列（100/110/122/244/44/83/86/118/128/138/139/134/135）时必须先解析 `chroma_format_idc`/scaling list，否则后面所有 ue(v) 全部错位，宽高会算成离谱值。
- 裁剪单位随 chroma_format 变化：4:2:0 → CropUnitX=2、CropUnitY=2*(2-frame_mbs_only)；1080p 的 SPS 通常带 `frame_cropping`（1920x1088 → 裁成 1080），不处理会输出 1088。
- x264 对 RGB 测试源默认可能选 High 4:4:4（profile_idc=244），不是 High(100)，排查时别按 100 硬编码。
- emulation prevention：`00 00 03` 必须在解析前去掉，否则 Exp-Golomb 读到错误位。

### B14. YOLO / SimdYolo
- `YoloDetector` 会话**不支持并发推理**（同会话重入直接抛异常），必须信号量串行化；也不要每帧 `new` 一个 detector（模型加载 ~300ms，复用会话）。
- `Predict`/`Detect` 只吃**已解码的 RGB24/BGR24 + 宽高 + stride**，图片解码由调用方做。本项目用 ffmpeg 抽帧输出 P6 PPM（`-f image2pipe -vcodec ppm`）再自己解析，避免再引 ImageSharp/SkiaSharp（AOT/许可证都干净）。
- `.pt` 的输入宽高必须是 32 的倍数，否则加载/推理失败。
- PowerShell 的 `>` 重定向默认 UTF-16，会把 PPM/二进制写坏（表现为头前面多 BOM、长度翻倍）；验证时用 `cmd /c` 或 `Start-Process -RedirectStandardOutput`。代码里直接读 ffmpeg stdout 管道，不受此影响。
- 模型（6MB+）不入库：`.env` 的 `ZLM_YOLO_MODEL` 指向本机文件，相对路径按 exe 目录解析；常规 CI 不跑真实模型，冒烟测试用 `ZLM_YOLO_TEST_MODEL` 环境变量开启。
- `yoloDetect` 的 `url=` 模式每请求起一个 ffmpeg 进程（约 0.2~1s），**不要**用在逐帧检测上；二期做按流定时检测时应该长驻一个解码器/抽帧器。
- 本机 30 个 ONNX 实测（SimdYolo 0.11.2）：**检测 / 分割 / 姿态全部可跑**；**分类（`*-cls`）与 OBB 不支持** —— cls 报 `YOLO head must be rank 3.`，obb 报 `operator 'Cos' unsupported`。`yoloInfo.models` 只是列目录，不代表每个都能推理，配置时避开这两类。
- 模型仓库用法：`ZLM_YOLO_MODEL_DIR=F:\ai\models\yolo` + `ZLM_YOLO_MODEL=yolov8s.onnx`（只写文件名）；`yoloDetect?model=yolov8m-person.onnx` 可按请求切模型，进程内最多缓存 2 个（LRU 淘汰，可用 `YoloOptions.MaxCachedModels` 调）。
- 首次推理包含模型加载（ONNX/CPU：~0.3-0.6s），之后的耗时才是纯推理（640×640 CPU 约 30~60ms）；压测/限速时别把首帧当稳态。
- 定时检测（`StreamYoloMonitor`）对**每一路在线流**按 `ZLM_YOLO_SAMPLE_INTERVAL_MS` 抽一帧：每次采样起一个 ffmpeg 进程读本地 HTTP-FLV，并用信号量限制同刻只跑一个采样。所以 `getMediaList` 里会看到一路流多出 1 个 reader——那是 YOLO 抽帧连接，不是异常。
- 抽帧靠 HTTP-FLV，所以 `http.port` 为 0 时定时检测直接跳过；`url=` 模式同理依赖 `.env` 的 `ZLM_FFMPEG_BIN`。
- 游戏/动画画面用 COCO 模型容易误检（PUBG demo 会报 backpack/tv/motorcycle）；专用模型（`yolov8m-person.onnx`）在低阈值下也可能漏检，测试时先 `yoloDetect?confidence=0.1` 看实际框，再定阈值。
- 结果叠加（`startYoloOverlay`）每路起**两个** ffmpeg：解码源 HTTP-FLV → stdout 原始 RGB24 → C# 画框 → 另一个 ffmpeg stdin 编码 x264 推回 `rtmp://127.0.0.1:1935/{app}/{stream}_yolo`。所以 `http.port` 与 `rtmp.port` 都必须开启，CPU 占用约等于「解码 + 一路 x264」。
- libx264 + yuv420p 要求宽高为偶数：代码会把 SPS 解析出的宽高向下取偶，奇数分辨率的源会被裁掉 1px（检测框按原图坐标画，肉眼无感）。
- 叠加流每帧画的是 `YoloResultStore` 里 5s 内的最近快照（默认 1s 采样），不是逐帧推理；所以框会跟手但略有延迟，属于预期。低于 5s 没有检测结果时输出纯净画面。
- 叠加流名固定为 `{stream}_yolo`，`StreamYoloMonitor` 会跳过 `*_yolo`，否则会对叠加结果再检测形成无限套娃；手动推一路叫 `xxx_yolo` 的流也会被跳过（不要再对它的结果做检测）。
- 内置 5×7 位图字体只有大写字母/数字/少量符号，标签显示为 `CLASSNAME 93%`（无类名时 `#7 50%`）；`scale=` 控制标签字号（默认 2）。
- 实测 720p 叠加在 CPU 上稳定 25fps、无积压；启动最初 1~2s 帧数偏低是 ffmpeg 分析/建 GOP，不是掉帧。
- `trackId` 由 `ObjectTracker` 在**定时检测**写入（按流一个实例）：同类别 + IoU ≥ 0.3 才算同一目标，连续丢失 5 次后丢弃、ID 不复用；分辨率变化会自动重建跟踪器，ID 从头开始。`yoloDetect` 是单图检测，`trackId` 恒为 0。
- 目标 1s 内位移超过框长/宽（或两次采样间穿插遮挡）时 IoU 可能掉到阈值以下，会分配新 ID——这是轻量跟踪的预期取舍，需要长时轨迹再上卡尔曼/ReID。
- 停止叠加会同时杀掉两个 ffmpeg，`tencent_yolo` 在本机 RTMP 断开后由服务端自动回收（`getMediaList` 里消失）；服务端崩溃时残留 ffmpeg 子进程需人工清理（后续可按 PID 记账）。

### B15. 录制与转推（M8）
- 录像目录：`ZLM_RECORD_ROOT`（.env，优先）> `protocol.mp4_save_path` > `www`，相对路径按 **exe 目录**解析。实测路径里会带 `./www` 这种未归一化段（`F:\...\net10.0\./www\live\...`），Windows 能正常打开，展示时可用 `Path.GetFullPath` 归一。
- 文件布局：`{root}/{app}/{stream}/{yyyy-MM-dd}/{HH-mm-ss}-{切片序号}.{mp4|flv}`；`max_second` 默认 `protocol.mp4_max_second`（夹在 10~86400 秒），到点自动切片，序号从 001 递增。
- MP4 的 moov 写在**文件末尾**：`stopRecord` 之前文件不可播（没有索引），进程崩溃会丢当前切片（之前切好的不受影响）。所以别拿正在录的文件做线上播放，要播就录完或后续做 fMP4/HLS。
- MP4 没有 faststart（moov 不在前面）：本地/VLC/ffmpeg 播放没问题，不适合边下边播；`ffprobe` 显示 `duration`、`-ss` 定位都正常（实测 24.9s 文件 `-ss 15` 成功）。
- FLV 录像追加式写入：崩溃最多丢最后一个 tag，能直接播；但没索引，不能精确 seek，网页 `<video>` 也不支持（要 flv.js）。
- 音频参数集来自 `MediaTrack.AudioSpecificConfig`（`FlvFrameIngest` 收到 AAC sequence header 时写入），MP4 写 `esds`、FLV 写 AAC sequence header；不要用 FLV header 的 sound rate 位判断真实采样率——AAC 的采样率看 ASC（实测源 44100/48000 都可能出现）。
- 源没有音频时录像自动退化成纯视频（FLV header flags=0x01），不会因为缺 AAC 配置失败。
- 自动录制：`.env`/ini 打开 `protocol.enable_mp4`（MP4）与 `protocol.enable_flv_record`（FLV，自定义键）后，后台每 2s 巡检在线流并补录，跳过 `*_yolo`；录像会占一个 reader，所以流不会被空闲回收（这是预期行为）。
- 同一路流重复 `startRecord` 会先停旧的再重开；`stopRecord` 之后 `isRecording` 返回 `data=null`，旧路径不要再引用。
- 转推只支持 **RTMP 目标**（`rtmp://host[:port]/app/stream`），RTSP `ANNOUNCE/RECORD` 推流还没做；失败不自动重连，需要重新 `startPush`。
- 推回本机自测时目标流名必须不同（如 `tencent_copy`），推成同名会和源流冲突；`getMediaList` 里能看到 copy 流（H.264/AAC），`getPushList` 的 `frames` 持续增长。
- 从**刚启动的转推流**抓帧，ffmpeg 可能报 `co located POCs unavailable` / `mmco: unref short failure`（连上时正好在 GOP 中间），先 `-c copy` 落成 FLV 再完整解码即可验证——实测无错误；不是推流损坏。
- 录像/转推每个任务各占 1 个 reader，`readerCount` 会相应 +1/+2；排查"谁在占流"时先看这两个。

## C. 待确认问题
- [x] AOT 下 Kestrel（`CreateSlimBuilder`）已验证零告警；WebSockets 待 M4 实测（若有限制再评估自研 HTTP/1.1+WS）。
- [x] NLog 在 AOT 下可用（保留程序集后控制台/文件均正常，exe 13.5MB）；是否改用结构化日志（JSON）待定。
- [ ] 一期是否需要 `www/` 播放页与 `postman/` API 集合同步移植？
- [ ] 是否需要同端口协议嗅探（ZLM 支持 HTTP/RTSP 共端口）？
- [ ] 是否需要鉴权 hook（`on_http_access`）预留扩展点？
- [ ] 目标部署平台（Windows 服务 / Linux systemd / Docker）与发布方式；是否需要在 `zlmsharp.ini` 缺失时自动生成（当前计划：生成）。
- [ ] 录像落地形态：MP4 分段（当前计划）还是同时输出 fMP4/HLS；是否需要录像回放 API。
- [x] YOLO 推理选型：用 **SimdYolo**（纯托管，不引 ONNX Runtime/CUDA）；`ZLM_YOLO_ENDPOINT` 作为外部推理服务的预留键，暂不使用。NativeAOT 与 Vulkan/Metal 路径待实测。
