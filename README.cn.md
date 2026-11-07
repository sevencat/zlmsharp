# zlmsharp

<img src="assets/app-icon.png" width="96" align="right" alt="zlmsharp" />

用 C#/.NET 8 复刻 [ZLMediaKit](https://github.com/ZLMediaKit/ZLMediaKit) 的流媒体核心：推流接入、媒体源分发、协议转换，以及一套与 ZLM 兼容的 HTTP API —— 一个单文件可执行程序 + 一个类库。

[English](README.md) | **简体中文**

[![NuGet](https://img.shields.io/nuget/v/Sevencat.ZlmSharp.MediaServer?label=nuget&color=blue)](https://www.nuget.org/packages/Sevencat.ZlmSharp.MediaServer)
[![NuGet downloads](https://img.shields.io/nuget/dt/Sevencat.ZlmSharp.MediaServer?label=downloads)](https://www.nuget.org/packages/Sevencat.ZlmSharp.MediaServer)

- RTMP 推/播（HEVC 走 Enhanced RTMP 的 `hvc1` 扩展头）、RTSP 推/播（TCP interleaved + UDP）、HTTP-FLV、WebSocket-FLV；视频 H.264 / H.265
- 每路流一个 `MediaSource`：环形缓冲、GOP 快照、按读者分发并统计各读者丢帧
- MP4 / FLV / HLS(TS) 录制、RTMP 转推、RTSP/RTMP 拉流代理
- hook（`on_publish` / `on_play` / `on_stream_none_reader`）、会话管理、无人观看回收
- 可选 YOLO 检测 / 叠加（纯托管推理，无原生依赖，可跑在任意在线流上）
- 面向 NativeAOT 发布，配置绑定不依赖反射

---

## 快速开始

目标框架为 `net8.0`，需要 .NET 8 SDK 或更高（`global.json` 要求不低于 `8.0.100`）。

```bash
git clone <repo> zlmsharp && cd zlmsharp
dotnet build zlmsharp.sln
dotnet run --project src/ZlmSharp.Cli
```

首次启动会在 exe 同目录（`src/ZlmSharp.Cli/bin/Debug/net8.0/`）生成 `zlmsharp.ini` 与 `.env`，然后监听：

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

### 使用 ffmpeg dll（进程内 libav）

默认的 `zlmsharp.exe` 每次取帧/转码都起一个 `ffmpeg.exe` 子进程。要走 **FFmpeg.AutoGen 8.x + ffmpeg dll**、在进程内直接解码/编码（不起子进程、不走管道），需要换宿主：用 `zlmsharp-libav.exe`。

- 只有 `zlmsharp-libav.exe` 能用 dll；`zlmsharp.exe` 是 NativeAOT 构建，不含 FFmpeg.AutoGen，不能切换。
- 业务代码完全一致：参数、`zlmsharp.ini`、`.env`、日志、端口、API、AI 分析都不变，只换媒体实现。

#### 1. 准备 dll

把 ffmpeg **8.x 的 shared 构建**（与本仓库 `FFmpeg.AutoGen 8.1.0` 对应）里的这些 dll 与 `ffmpeg.exe` 放在**同一目录**：

```
avcodec-62.dll  avformat-62.dll  avutil-60.dll  avfilter-11.dll
avdevice-62.dll swscale-9.dll    swresample-6.dll
```

实测最小集合是前五个（`avcodec` / `avformat` / `avutil` / `swscale` / `swresample`），抓帧、解码、libx264 推流都够用；`avfilter` / `avdevice` 当前流程不调用，留空也行，嫌麻烦就把整个 shared 构建目录（含 exe）一起拷。要求 **x64 且与进程位数一致**；官方 shared 构建还依赖 MSVC 运行时（`vcruntime140.dll` 等，装 VC++ Redistributable 即可）。

#### 2. 配置 dll 目录

程序取 `ZLM_FFMPEG_BIN` 的**所在目录**作为 dll 目录，所以三种写法任选：

```dotenv
# .env —— 推荐：写绝对路径，两个宿主共用同一份 .env
# exe 版直接把它当 ffmpeg.exe；libav 版只取它的目录
ZLM_FFMPEG_BIN=E:\program\ffmpeg\81\ffmpeg.exe
```

```dotenv
# 也可以直接写目录（仅 libav 版认这种写法，exe 版会启动失败）
ZLM_FFMPEG_BIN=E:\program\ffmpeg\81
```

- 进程环境变量优先于 `.env`：`$env:ZLM_FFMPEG_BIN='E:\program\ffmpeg\81\ffmpeg.exe'` 可以临时覆盖。
- 相对路径按**当前工作目录**解析（不是 exe 目录），所以一定写绝对路径。
- 留空时 dll 目录退化为当前工作目录：`dotnet run` 从仓库根启动、或从别处启动 exe 都会找不到 dll，**不要依赖空值**。
- 编程式（自己写宿主 / 嵌入式）可直接指定，跳过 `.env`：

```csharp
// 方式 A：显式指定 ffmpeg.exe 路径或 dll 目录（内部即 Replace 掉默认的 FfmpegToolkit）
builder.Services.AddAutoGenMediaToolkit(@"E:\program\ffmpeg\81");

// 方式 B：嵌入式宿主里，先用默认实现注册，再整体替换
builder.Services.AddZlmSharp(options => options.RegisterStreamListeners = true);
builder.Services.AddZlmSharpExtra();
builder.Services.AddAutoGenMediaToolkit();
```

（本仓库的 `zlmsharp-libav` 走的是 `CliApplication.ConfigureMedia`，那是 `internal` 扩展点，只有像它那样把 `CliApplication.cs` 链接进来的项目才用得到。）

#### 3. 启动

```bash
dotnet run --project src/ZlmSharp.Cli.Libav        # 开发
dotnet publish src/ZlmSharp.Cli.Libav -c Release   # 发布（JIT，不能 AOT）
# 产物 zlmsharp-libav.exe，配置/参数与 zlmsharp.exe 完全相同
```

`.env` 会从 `src/ZlmSharp.Cli/.env` 复制到输出目录（csproj 里的 `None Include`），所以改那一份即可对两个宿主同时生效；发布后也可直接改 exe 同目录的 `.env`。

#### 4. 确认生效

1. 起流后调用一次取帧或叠加（如 `startOverlay`、`analyzeDetect?analyzer=ocr`）。
2. `Get-Process ffmpeg -ErrorAction SilentlyContinue` 应为空；同时叠加推流、抓图、录像一切正常，就说明走的是 dll。
3. dll 目录不对时，第一次用到媒体会抛 `加载 ffmpeg dll 失败（目录 …）`，日志里能看到具体目录。

#### 5. 两版差异

| 项 | `zlmsharp.exe`（默认） | `zlmsharp-libav.exe`（dll） |
| --- | --- | --- |
| 媒体进程 | 每路抓帧/转码一个 `ffmpeg.exe` | 无子进程，dll 在进程内 |
| 依赖 | PATH 或 `ZLM_FFMPEG_BIN` 指向的 `ffmpeg.exe` | 同目录的 ffmpeg 8.x dll |
| 发布 | NativeAOT，单文件 | JIT，需要 .NET 运行时与 dll |
| `FfprobePath` | 配置值（但当前没有流程调用） | 恒为空 |
| 退出码 | 由 ffmpeg 子进程给出 | 由编码线程给出 |
| 行为 | 抓单帧、常驻采样、顺序解码、libx264 叠加推流 | 完全对齐 |

`samples` 与 `tests` 仍使用默认的 exe 实现。

## zlmsharp.exe：媒体实现、命令行与完整配置

### 用的哪套实现、哪个版本

`src/ZlmSharp.Cli`（产物 `zlmsharp.exe`）是默认宿主，当前版本 **0.1.4**（`-v` 输出 `zlmsharp 0.1.4 (build ..., 分支/提交)`；版本号定义在 `Directory.Build.props` 的 `Version`，以 `-v` 实际输出为准）。

媒体能力全部收在 `IMediaToolkit` 之后，默认实现是 `FfmpegToolkit`：

- **子进程 + 管道**：每次抓单帧 / 常驻采样 / 顺序解码 / 叠加推流都 `Process.Start` 一个 `ffmpeg.exe`，用 stdin/stdout 传 PPM 或 RGB24 裸帧。进程内不加载任何 ffmpeg dll，也不引用 FFmpeg.AutoGen。
- **ffmpeg.exe 版本**：代码不做版本校验，只用 `-f image2pipe -vcodec ppm`（抓帧）、`-f rawvideo -pix_fmt rgb24`（解码）、`libx264` + `-f flv`（叠加推流）等基础能力；ffmpeg 4.x ~ 8.x 的官方构建都带，本仓库实测为 **8.x**（例如 `E:\program\ffmpeg\81\ffmpeg.exe`）。自编译 ffmpeg 时务必保留 libx264 与 ppm / rawvideo / flv。
- **ffprobe**：`ZLM_FFPROBE_BIN` 会被解析进 `IMediaToolkit.FfprobePath`，但当前版本没有任何流程调用它，可以不装。
- **进程内版本**：`src/ZlmSharp.Cli.Libav`（`zlmsharp-libav.exe`）用 FFmpeg.AutoGen 8.1.0 + ffmpeg 8.x dll 在进程内做同样的事，dll 的放置与 `ZLM_FFMPEG_BIN` 配置见上文「使用 ffmpeg dll（进程内 libav）」。两者只共享上层逻辑，媒体实现互不依赖。

上层（RTSP / RTMP / HTTP-FLV / 录制 / AI 分析）只依赖 `IMediaToolkit` 接口，注册自己的实现即可整体替换：

```csharp
builder.Services.AddSingleton<IMediaToolkit>(provider =>
    new MyToolkit(provider.GetRequiredService<AppOptions>().Env));
```

### 命令行与退出码

```bash
zlmsharp                                    # 读 exe 同目录的 zlmsharp.ini + .env
zlmsharp -c ./conf/zlmsharp.ini -e ./conf/.env
zlmsharp -v                                 # 版本 + 构建时间 + 分支/提交
zlmsharp -h                                 # 用法
```

| 参数 | 说明 |
| --- | --- |
| `-c`, `--conf <path>` | 指定 `zlmsharp.ini`。**指定的文件不存在会直接报错退出**（不会自动生成） |
| `-e`, `--env <path>` | 指定 `.env`，规则同上 |
| `-d`, `--daemon` | 仅为兼容 ZLM 运维脚本保留；当前仍前台运行，只打一条日志 |
| `-v`, `--version` | 打印版本后退出 |
| `-h`, `--help`, `-?` | 打印用法后退出 |
| 未知参数 | 打印错误 + 用法，退出码 2 |

| 退出码 | 含义 |
| --- | --- |
| 0 | 正常退出（收到 Ctrl+C / SIGTERM） |
| 1 | 配置加载失败、宿主初始化失败、运行期异常 |
| 2 | 命令行参数错误 |
| 3 | HTTP 端口无法监听（被占用，或 Windows 非管理员绑定 <1024） |

### 首次启动与路径规则

首次启动会在 exe 同目录（`AppContext.BaseDirectory`，即 `bin/Debug/net8.0/` 或发布目录）生成两个文件，并打印生成提示：

- `zlmsharp.ini`：业务配置，见下方完整表格；注释与默认值就是内置模板。
- `.env`：机器相关 / 敏感 / 外部工具路径（ffmpeg、模型、录像根目录）。

ini 里所有相对路径（`./www`、`./log`、`./WebUI`、`./www/snap/`）都相对 **exe 目录**，不是当前工作目录；用 `-c` 把配置放到别处也不会改变这一点。

### 配置优先级与语法

```
进程环境变量  >  .env  >  zlmsharp.ini  >  内置默认值
```

- ini 以 **内置默认值为基底**再做覆盖，所以升级新增的键不会缺省；无法识别的键直接忽略，取值越界会被钳制/重置并打启动警告（例如 `[http] port=70000`）。
- 支持 `[section]`、`key=value`、整行 `;` / `#` 注释；同名键后者生效；键名大小写不敏感。
- 布尔值写 `0/1`（也接受 `true/false`、`on/off`、`yes/no`）。
- 端口写 `0` 表示 **不监听该协议**（HTTP 为 0 时只跑 RTSP/RTMP）。
- `getServerConfig` / `setServerConfig` 能读写的键以 `ConfigSchema` 白名单为准；下表标注「仅 ini」的项只能用文件配置（如 `api.pageToken`）。

### `[api]`

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `apiDebug` | `0` | 打印每次 HTTP API 请求与回复 |
| `secret` | `035c73f7-...` | 敏感接口的 `secret` 参数；来自 `127.0.0.1` / `::1` 的请求免校验，清空该键则关闭校验 |
| `pageToken` | 空（仅 ini） | 管理页面 `/zlm/admin` 的访问令牌，见「管理页面鉴权」 |
| `snapRoot` | `./www/snap/` | 截图根目录（预留） |
| `defaultSnap` | `./www/logo.png` | 默认截图文件（预留） |
| `downloadRoot` | `./www` | `downloadFile` 可访问的根目录 |

### `[general]`

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `enableVhost` | `0` | 是否启用虚拟主机；开启后流标识带 vhost |
| `flowThreshold` | `1024` | 触发流量统计事件的阈值（KB） |
| `maxStreamWaitMS` | `15000` | 播放端等待流注册的最长时间（毫秒） |
| `streamNoneReaderDelayMS` | `20000` | 无人观看多久后关闭流（毫秒），配合 `protocol.auto_close` / hook |
| `resetWhenRePlay` | `1` | 无人观看后重新推流时是否重置时间戳 |
| `mergeWriteMS` | `0` | 合并写毫秒（0=不合并） |
| `gop_cache_ms` | `0` | 新观众补发 GOP 的时间窗口（毫秒）；0=完整 GOP（秒开，延迟≈相机 GOP），设 1000 之类可降延迟 |
| `catch_up_pending_frames` | `0` | 播放端积压超过该帧数就丢积压跳到最近关键帧（追赶实时）；0=关闭，建议 150~300 |
| `mediaServerId` | `your_server_id` | 媒体服务器 id |
| `listen_ip` | `::` | 监听地址，多个用逗号分隔；`::` / `0.0.0.0` 表示全部 |
| `wait_track_ready_ms` | `10000` | 等待 track ready 的超时（毫秒） |
| `wait_audio_track_data_ms` | `1000` | 等待首个音频数据的超时（毫秒） |
| `wait_add_track_ms` | `3000` | 等待 addTrack 完成的超时（毫秒） |
| `unready_frame_cache` | `100` | 未就绪阶段缓存的帧数 |
| `broadcast_player_count_changed` | `0` | 是否广播观众数变化 |

### `[http]`

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `port` | `8080` | HTTP/API 与 HTTP-FLV 端口；`0`=不监听。ZLM 默认 80，这里改成 8080 以免 Windows 非管理员无法绑定 |
| `sslport` | `443` | HTTPS 端口（预留） |
| `rootPath` | `./WebUI` | 静态站点目录（管理页面是内嵌的，不依赖它） |
| `charSet` | `utf-8` | 默认字符集 |
| `keepAliveSecond` | `30` | HTTP keep-alive 超时（秒） |
| `maxReqSize` | `40960` | 请求头大小上限（字节） |
| `maxUploadSize` | `1073741824` | 上传体上限（字节，默认 1 GiB） |
| `sendBufSize` | `65536` | socket 发送缓冲（字节） |
| `dirMenu` | `1` | 是否允许目录浏览 |
| `virtualPath` | 空 | 虚拟目录前缀 |
| `forbidCacheSuffix` | 空 | 禁止缓存的后缀列表 |
| `allow_origins` | `*` | 跨域允许来源 |
| `allow_ip_range` | 内网段列表（见下） | 允许访问的 IP/网段，支持 `a-b` 区间写法；默认值为 `::1,127.0.0.1,172.16.0.0-172.31.255.255,192.168.0.0-192.168.255.255,10.0.0.0-10.255.255.255`，留空表示不限制 |
| `forwarded_ip_header` | 空 | 从哪个请求头取真实 IP（反代场景） |

### `[rtsp]`

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `port` | `554` | RTSP 端口；`0`=不启用；非管理员改 `8554` |
| `sslport` | `0` | RTSPS 端口（0=关闭） |
| `authBasic` | `0` | 是否启用 Basic 鉴权 |
| `directProxy` | `1` | 拉流代理是否直接透传（不转码） |
| `handshakeSecond` | `15` | 握手超时（秒） |
| `keepAliveSecond` | `15` | 保活超时（秒） |
| `lowLatency` | `0` | 低延迟模式 |
| `rtpTransportType` | `-1` | RTP 传输：-1 自动、0 UDP、1 TCP |

### `[rtmp]`

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `port` | `1935` | RTMP 端口；`0`=不启用 |
| `sslport` | `0` | RTMPS 端口（0=关闭） |
| `directProxy` | `1` | 拉流代理是否直接透传 |
| `handshakeSecond` | `15` | 握手超时（秒） |
| `keepAliveSecond` | `15` | 保活超时（秒） |
| `enhanced` | `1` | 是否支持 Enhanced RTMP（HEVC 的 `codecId=12`/`hvc1` 两种 FLV 头） |

### `[protocol]`

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `modify_stamp` | `2` | 时间戳覆盖模式：0 不改、1 改首帧、2 全部重算 |
| `enable_audio` | `1` | 转协议输出是否带音频 |
| `add_mute_audio` | `1` | 只有视频时是否补静音 AAC，保证播放器兼容 |
| `auto_close` | `0` | 无人观看是否直接关流（关闭时走 `streamNoneReaderDelayMS`） |
| `continue_push_ms` | `15000` | 断流续推保护时间（毫秒），短暂断开不立即关流 |
| `paced_sender_ms` | `0` | 平滑发送间隔（毫秒，0=关闭） |
| `enable_hls` | `0` | 是否生成 HLS |
| `enable_hls_fmp4` | `0` | 是否生成 HLS-fMP4 |
| `enable_mp4` | `0` | 是否录制 MP4 |
| `enable_flv_record` | `0` | 是否同时录 FLV 原始档（MP4 为正式录像） |
| `enable_rtsp` | `1` | 是否生成 RTSP 输出 |
| `enable_rtmp` | `1` | 是否生成 RTMP/FLV 输出 |
| `enable_ts` | `0` | 是否生成 TS |
| `enable_fmp4` | `0` | 是否生成 fMP4 |
| `hls_demand` / `rtsp_demand` / `rtmp_demand` / `ts_demand` / `fmp4_demand` | `0` | 按需生成：有人看才转该协议 |
| `mp4_max_second` | `3600` | MP4 单文件最长秒数，超过自动切片 |
| `mp4_save_path` | `./www` | 录像保存根目录（可用 `ZLM_RECORD_ROOT` 覆盖） |
| `hls_save_path` | `./www` | HLS 输出目录 |

### `[proxy]`（拉流代理）

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `enable` | `0` | 是否启用启动列表 |
| `max_concurrency` | `16` | 并发拉流上限（钳制 1~256） |
| `retry_delay_ms` | `2000` | 断线重连间隔（毫秒，钳制 200~60000） |
| `list` | 空 | 启动即拉：`app/stream=url`，多项 `;` 分隔，支持 `rtmp://` / `rtsp://` |
| `keep_on_none_reader` | `0`（模板未含此键，需手工加） | 启动列表里的代理是否常驻（没人看也不回收）；也可用 `addStreamProxy?keep=1` 按单路指定 |
| `on_demand_list` | 空 | 按需拉流列表：有人点播才连源站 |
| `on_demand_idle_ms` | `10000` | 最后一个观众离开后保持多久再停（毫秒，反抖动） |
| `on_demand_max_wait_ms` | `30000` | 启动后多久仍无观众就停（毫秒），应大于 `maxStreamWaitMS` |

### `[hls]`

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `seg_duration` | `5` | 单切片秒数（按关键帧对齐，钳制 1~60） |
| `seg_num` | `5` | 播放列表保留片数（钳制 2~100） |

### `[hook]`（媒体事件回调）

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `enable` | `0` | 是否启用 |
| `on_publish` | 空 | 推流前鉴权：POST JSON，返回 `{"code":0}` 放行 |
| `on_play` | 空 | 播放前鉴权，规则同上 |
| `on_stream_none_reader` | 空 | 无人观看时回调，返回 0 表示允许关流 |
| `timeoutSec` | `10` | 单次请求超时（秒） |
| `retry` | `0` | 失败重试次数 |
| `retry_delay` | `1000` | 重试间隔（毫秒） |
| `fail_open` | `0` | hook 不可用（超时/网络错误）时是否放行；默认拒绝，避免鉴权被绕过 |

### `[log]`（zlmsharp 扩展段）

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `level` | `2` | 0 trace、1 debug、2 info、3 warn、4 error、5 critical、6 off；越界重置为 2 |
| `console` | `1` | 是否输出到控制台 |
| `file` | `./log/zlmsharp.log` | 日志文件；置空则不写文件 |
| `maxFileSizeMB` | `20` | 单文件上限（MB） |
| `maxFiles` | `5` | 保留文件数 |

### `.env` 全部键

`.env` 放机器相关 / 敏感 / 外部工具路径；键名大小写不敏感，支持 `#` 注释、`export` 前缀、单双引号与 `${NAME}` 引用。**进程环境变量优先于文件值**，文件里的值会被注入进程环境（ffmpeg 子进程也能读到）。

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `ZLM_FFMPEG_BIN` | 空（用 PATH 里的 `ffmpeg`） | ffmpeg.exe 绝对路径；`zlmsharp-libav` 取它的**所在目录**作为 dll 目录（也可直接写目录，仅 libav 版认）。明细见「使用 ffmpeg dll（进程内 libav）」 |
| `ZLM_FFPROBE_BIN` | 空（用 PATH 里的 `ffprobe`） | 当前版本不调用，可不配 |
| `ZLM_RECORD_ROOT` | 空（用 `protocol.mp4_save_path`） | 录像根目录，覆盖 ini |
| `ZLM_EXTRA_ENABLE` | `1` | AI 分析扩展总开关：0=关闭定时分析与叠加（单图分析接口仍可用） |
| `ZLM_ANALYSIS_SAMPLE_INTERVAL_MS` | 未配置时取 `ZLM_YOLO_SAMPLE_INTERVAL_MS`（1000） | 分析调度全局采样间隔（毫秒） |
| `ZLM_ANALYSIS_ENABLED` | 空=全部已注册分析器 | 分析器白名单，逗号分隔，如 `yolo,ocr` |
| `ZLM_ANALYSIS_BUSY_POLICY` | `skip` | 分析器忙时：`skip` 跳过本轮（不积压）、`queue` 排队（延迟会累积） |
| `ZLM_YOLO_MODEL_DIR` | 空 | 模型目录；`ZLM_YOLO_MODEL` 只写文件名时在此查找 |
| `ZLM_YOLO_MODEL` | 空=关闭 YOLO | 模型路径（`.pt`/`.onnx`）；相对路径按 exe 目录解析 |
| `ZLM_YOLO_ENDPOINT` | 空 | 预留：外部推理服务地址（当前走本地 SimdYolo） |
| `ZLM_YOLO_DEVICE` | `cpu` | 推理设备：`cpu` / `vulkan` / `metal` / `auto` |
| `ZLM_YOLO_CONFIDENCE` | `0.25` | 置信度阈值（0~1） |
| `ZLM_YOLO_INPUT_WIDTH` / `ZLM_YOLO_INPUT_HEIGHT` | `640` / `640` | 模型输入尺寸（`.pt` 要求 32 的倍数） |
| `ZLM_YOLO_CPU_THREADS` | `0` | CPU 推理线程上限，0=库默认 |
| `ZLM_YOLO_SAMPLE_INTERVAL_MS` | `1000` | 同一路流两次检测的最小间隔（毫秒），0=不限速 |
| `ZLM_OCR_ENABLE` | `0` | OCR 分析开关（CPU 占用明显，默认关） |
| `ZLM_OCR_MODEL` | `tiny` | 内置模型（PP-OCRv6 tiny，随包内置）；自定义模型齐全时忽略 |
| `ZLM_OCR_DET_MODEL` / `ZLM_OCR_REC_MODEL` / `ZLM_OCR_DICT` | 空 | 自定义检测/识别模型与字典，三者齐全时优先于内置模型 |
| `ZLM_OCR_CLS_MODEL` | 空 | 可选方向分类模型 |
| `ZLM_OCR_MIN_SCORE` | `0.3` | 识别分数下限（0~1），低于该值的文本行丢弃 |
| `ZLM_OCR_USE_CLS` | `1` | 是否用方向分类模型纠正倒置文本 |
| `ZLM_OCR_LINE_WORKERS` | `1` | 文本行并行识别线程数（钳制 1~64） |
| `ZLM_OCR_SAMPLE_INTERVAL_MS` | `2000` | 同一路流两次 OCR 的最小间隔（毫秒） |
| `ZLM_OVERLAY_SUFFIX` | `_ai` | 叠加派生流后缀（`live/test` → `live/test_ai`） |
| `ZLM_OVERLAY_FRAME_POLICY` | `reuse` | 叠加取结果：`reuse` 每帧都用最近结果（不跳帧，标注可能滞后）、`skip` 只用新鲜结果 |
| `ZLM_OVERLAY_RESULT_MAX_AGE_MS` | `0` | 分析结果可复用时长（毫秒）；0=按策略默认（reuse 5000 / skip 200） |

### 最小可用配置示例

```ini
[general]
mediaServerId=demo-01
listen_ip=0.0.0.0
[http]
port=8080
[rtsp]
port=8554          ; 非管理员也能绑
[rtmp]
port=1935
[protocol]
enable_audio=1
add_mute_audio=1
[log]
level=2
console=1
file=./log/zlmsharp.log
```

```dotenv
# .env：只写机器相关项，业务配置仍留在 ini
ZLM_FFMPEG_BIN=E:\program\ffmpeg\81\ffmpeg.exe
ZLM_RECORD_ROOT=D:\records
```

## 配置

每一项的含义、默认值、合法取值与 `.env` 全量清单都在上文「zlmsharp.exe：媒体实现、命令行与完整配置」里；这里只讲两个文件的生成与加载规则。

两个文件都可选，首次启动自动生成：

- `zlmsharp.ini`：业务配置，段名/键名与 ZLM `conf/config.ini` 对齐（`[api]`、`[general]`、`[http]`、`[rtsp]`、`[rtmp]`、`[protocol]`、`[hook]`、`[hls]`），另有 zlmsharp 扩展段 `[proxy]`（拉流代理）与 `[log]`（NLog）。
- `.env`：机器相关/敏感项：ffmpeg 路径、录像根目录、YOLO 模型目录等；进程环境变量优先于 `.env`。

ini 解析支持 `[section]`、`key=value`、整行 `;` / `#` 注释、同名键后者生效；用户文件是叠加在内置默认值之上的，升级新增的键不会缺省。取值越界会被钳制并给出启动警告，无法识别的键直接忽略。

### 管理页面鉴权

内置控制台挂在 `/zlm/admin`（单文件 HTML 内嵌在程序集里）。是否要求验证是可选的：

- `[api] pageToken=...`：设置后打开页面必须带 `?token=...`（或请求头 `X-ZlmSharp-Token`）；校验通过会写入 7 天有效的 `HttpOnly` cookie，后续刷新/直接打开不用再带。留空（默认）表示公开访问。
- 自定义策略（SSO、IP 白名单、动态口令等）：在 `AddZlmSharp` 之后注册 `IAdminPageAuthorizer` 即可完全接管，默认的 token 校验不再生效：

```csharp
builder.Services.AddSingleton<IAdminPageAuthorizer, MyAdminPageAuthorizer>();
```

## HTTP API

返回体统一是 ZLM 风格的 `{"code":0,"msg":"success","data":...}`。`GET /index/api/getApiList` 返回已实现的接口。非回环地址调用必须带 `secret=`（与 `[api] secret` 一致，清空该键则关闭校验）；来自 `127.0.0.1` / `::1` 的请求与 ZLM 一样免鉴权。

| 接口 | 作用 |
| --- | --- |
| `version`、`getApiList` | 构建信息、接口清单 |
| `getMediaList` | 在线流：编码、分辨率、帧率、读者数、存活时长、`originType`、`dropFrames`；可按 `vhost`/`app`/`stream`/`schema` 过滤 |
| `close_streams` | 关掉匹配的流，返回 `count` / `closed`；跳过受管流——拉流代理只能用 `delStreamProxy` 停，叠加输出流只能用 `stopOverlay` 停 |
| `getSessionList`、`kick_session` | 查看/踢掉推流与播放会话（`id=` 精确踢，或按条件批量） |
| `startRecord`、`stopRecord`、`isRecording`、`getRecordList` | 录像控制与任务列表：`type=1` MP4、`2` FLV、`3` MP4+FLV、`4` HLS、`5` 全部 |
| `getMp4RecordFile`、`deleteRecordFile` | 浏览/删除录像文件（`app`+`stream`+`period`+`path`），越界路径拒绝 |
| `GET /record/{相对路径}` | 直接播放/下载录像文件（支持 Range），路径相对录像根目录，无需 secret |
| `startPush`、`stopPush`、`getPushList` | RTMP 转推 |
| `addStreamProxy`、`delStreamProxy`、`getProxyList` | 拉流代理（RTMP/RTSP 源 → 本地流），列表含源地址、在线状态、`keepOnNoneReader` 与 `onDemand`；普通代理没人看时照常回收，`keep=1` 则一直拉、直到 `delStreamProxy`（`[proxy] keep_on_none_reader` 设启动列表的默认值）；`on_demand=1` 只登记为按需拉流（有人点播才拉，见「按需拉流」） |
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

### 按需拉流（有人看才拉）

预先写好 `app/stream=url`，宿主启动时不连源站；有播放端（HTTP-FLV / WS-FLV / RTMP play / RTSP DESCRIBE、PLAY）
点播这些流时才拉起，观众全部离开后自动断开，下次点播再拉。断流期间只要还有观众，就按 `retry_delay_ms` 一直重连。

```ini
[proxy]
on_demand_list=live/cam1=rtsp://192.168.1.10:554/stream1; live/cam2=rtmp://example.com/live/demo
on_demand_idle_ms=10000      # 最后一个观众离开后保持多久再断开（接住刷新页面的抖动）
on_demand_max_wait_ms=30000  # 启动后一直没人看（源不可用/观众已走）多久就断开
```

- 按需流不参与 `auto_close` / `on_stream_none_reader` 回收，生命周期只由「有没有观众」决定，避免刚拉起就被摘流。
- 按需流默认不做 AI 定时分析：分析抽帧本身就是一个观看者，会让按需流永远停不下来；需要分析的流请改用常驻代理 `list`。

除了配置文件，也可以在运行期用 API 或代码登记：

```bash
# 登记一路按需拉流（on_demand=1：只登记，不立即拉）
curl "http://127.0.0.1:8080/index/api/addStreamProxy?app=live&stream=cam3&on_demand=1&url=rtsp%3A%2F%2F192.168.1.11%3A554%2Fstream1"
# 查看：按需未触发的项 onDemand=true、online=false
curl "http://127.0.0.1:8080/index/api/getProxyList"
# 删除登记（和删除普通代理同一个接口）
curl "http://127.0.0.1:8080/index/api/delStreamProxy?key=__defaultVhost__%2Flive%2Fcam3"
```

```csharp
// 嵌入式场景：注入 IOnDemandProxy 动态增删
onDemandProxy.Register(StreamKey.Create(null, "live", "cam3"), "rtsp://192.168.1.11:554/stream1");
onDemandProxy.Unregister(StreamKey.Create(null, "live", "cam3"));
```

## 录制

- `startRecord?app=&stream=&type=&max_second=` 对在线流开始录制；文件落在 `ZLM_RECORD_ROOT`（回退 `protocol.mp4_save_path`）下的 `app/stream/yyyy-MM-dd/`。
- 接口返回的 `mp4Path` / `flvPath` / `hlsPath` 是**相对录像根目录的归一化路径**（`/` 分隔、不含盘符与根前缀，如 `live/cam/2026-11-04/20-15-08-001.mp4`），拼 `http://host:port/record/{路径}` 即可播放或下载（`getMp4RecordFile` 的 `rootPath` 同理）。
- MP4 走 AVCC（`avcC` + `esds`），FLV 是追加式（崩溃可播），HLS 按关键帧对齐产出 `hls-000001.ts…` + `hls.m3u8`（`[hls] seg_duration`、`seg_num`）。
- 推流端断线重推后会自动续录；`protocol.enable_mp4` / `enable_flv_record` 打开时开机自动录（跳过 `*_ai` 叠加流）。

## AI 分析（可选，ZlmSharp.Extra）

核心库只保留契约与 HTTP 入口，取帧（ffmpeg）、推理、跟踪、绘框、叠加管线在 `ZlmSharp.Extra` 里，宿主显式 `AddZlmSharpExtra()` 接入；不接入时相关 API 按“未启用”返回。

```csharp
builder.Services.AddZlmSharp(...);          // 核心：协议 + API，零 AI 依赖
builder.Services.AddZlmSharpExtra(extra =>  // 可选：内置 YOLO + 采样调度 + 叠加管线
{
    extra.AddAnalyzer<MyOcrAnalyzer>(o => o.Outputs = AnalysisOutputs.None); // 只分析，不管结果
});
```

内置 YOLO 全部通过 `.env` 配置：`ZLM_YOLO_MODEL_DIR`、`ZLM_YOLO_MODEL`、`ZLM_YOLO_DEVICE`（`cpu`/`vulkan`/`metal`/`auto`）、`ZLM_YOLO_CONFIDENCE`、输入尺寸、线程数、采样间隔。模型懒加载并按 LRU 缓存；推理输入来自常驻 ffmpeg 解码器的最新一帧，结果按流保存并带稳定的 IoU `trackId`。`startYoloOverlay` / `startOverlay` 会拉起「解码 → 绘制 → 编码」管道，把 `{stream}_ai` 推回本服务。

第三方算法接入（详见 `extra-design.md`）：

- **L1 算法级（推荐）**：实现 `IStreamAnalyzer`（+ 可选 `IOverlayRenderer` / `IAnalysisSink`），只拿到 `FrameSample`（RGB24 帧）返回 `AnalysisResult` 图元，不碰 ffmpeg/HTTP。
- **L2 推理内核级**：实现 `IYoloBackendFactory` / `IYoloInferenceBackend`，复用内置 YOLO 的前后处理与模型缓存，只替换推理内核（如 ONNX Runtime / 厂商 SDK 适配器）。
- 结果去向三种：`Snapshot`（`analyzeResult` 可查）、`Snapshot|Overlay`（叠加成 `{stream}_ai` 流）、`None`（只跑分析，结果由算法自己消费）。目录扫描式插件加载暂未提供，第三方以编译期引用 + `AddAnalyzer` 接入（AOT 友好）。

通用端点：`getAnalyzerList`（分析器清单）、`analyzeDetect`（单图分析）、`startOverlay` / `stopOverlay` / `getOverlay`（通用叠加）；旧端点 `yoloInfo` / `yoloDetect` / `getYoloResult` / `startYoloOverlay`… 保持兼容。

全局开关与通用配置：`ZLM_EXTRA_ENABLE`、`ZLM_ANALYSIS_SAMPLE_INTERVAL_MS`、`ZLM_ANALYSIS_ENABLED`（白名单）、`ZLM_OVERLAY_SUFFIX`（默认 `_ai`）。

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

hook 请求体字段与 ZLM 一致（`mediaServerId`、`app`、`stream`、`vhost`、`schema`、`id`、`ip`、`port`、`user_agent`、`params`）；推/播 URL 上的查询参数（如 `?token=xxx`）原样放进 `params`，由外部服务判定。无人观看的流在 `general.streamNoneReaderDelayMS` 之后按 `protocol.auto_close` / `on_stream_none_reader` 的规则回收；正在录像的流与叠加流不参与回收。拉流代理默认同样参与回收（回收后会自动重连），只有建代理时带 `keep=1`（或 `[proxy] keep_on_none_reader=1`）才一直拉、直到 `delStreamProxy`。

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
  Core/ Codecs/ Configuration/ Protocols/{Flv,Rtmp,Rtsp}/ Http/ Recording/ Api/ Server/
  Ai/                    AI 契约（IStreamAnalyzer / IOverlayController / 结果模型）与顶层调用
src/ZlmSharp.Extra/     AI 实现（程序集 ZlmSharp.Extra）：ffmpeg 取帧、YOLO、叠加管线、分析调度
src/ZlmSharp.Media.AutoGen/ 可选媒体实现（FFmpeg.AutoGen 8.x）：进程内调 ffmpeg dll，不起 ffmpeg.exe
src/ZlmSharp.Cli/       可执行入口（产物 zlmsharp.exe，NativeAOT）
src/ZlmSharp.Cli.Libav/ 可执行入口（产物 zlmsharp-libav.exe，媒体实现为进程内 dll，JIT）
samples/…Sample.Web/    嵌入示例（未接 Extra，AI 端点按“未启用”降级）
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
