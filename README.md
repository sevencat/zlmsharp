# zlmsharp

<img src="assets/app-icon.png" width="96" align="right" alt="zlmsharp" />

A C#/.NET 8 port of the [ZLMediaKit](https://github.com/ZLMediaKit/ZLMediaKit) streaming core: ingest, media source fan-out, protocol conversion and a ZLM-compatible HTTP API — as a single small binary plus one class library.

**English** | [简体中文](README.cn.md)

[![NuGet](https://img.shields.io/nuget/v/Sevencat.ZlmSharp.MediaServer?label=nuget&color=blue)](https://www.nuget.org/packages/Sevencat.ZlmSharp.MediaServer)
[![NuGet downloads](https://img.shields.io/nuget/dt/Sevencat.ZlmSharp.MediaServer?label=downloads)](https://www.nuget.org/packages/Sevencat.ZlmSharp.MediaServer)

- RTMP publish / play (HEVC via the Enhanced RTMP `hvc1` ex-header), RTSP publish / play (TCP interleaved + UDP), HTTP-FLV, WebSocket-FLV; video is H.264 / H.265
- One `MediaSource` per stream: ring buffer, GOP snapshot, frame-level fan-out with per-reader drop accounting
- MP4 / FLV / HLS(TS) recording, RTMP relay push, RTSP/RTMP pull proxy
- Hooks (`on_publish` / `on_play` / `on_stream_none_reader`), session registry, idle-stream reclamation
- Optional YOLO detection / overlay on top of any live stream (pure managed inference, no native deps)
- NativeAOT publish friendly; no reflection-heavy configuration binding
-rtmp://liteavapp.qcloud.com/live/liteavdemoplayerstreamid
---

## Quick start

Requirements: .NET 8 SDK or later (`TargetFramework` is `net8.0`, `global.json` requires at least `8.0.100`).

```bash
git clone <repo> zlmsharp && cd zlmsharp
dotnet build zlmsharp.sln
dotnet run --project src/ZlmSharp.Cli
```

First start writes `zlmsharp.ini` and `.env` next to the executable (`src/ZlmSharp.Cli/bin/Debug/net8.0/`), then listens on:

| Service | Default port | Notes |
| --- | --- | --- |
| HTTP (API + HTTP-FLV) | 8080 | 80 needs admin/root on Windows; change `[http] port` if you want ZLM parity |
| RTSP | 554 | |
| RTMP | 1935 | |

CLI flags: `-c/--conf <ini>`, `-e/--env <file>`, `-v/--version`, `-h/--help`, `-d/--daemon` (accepted for ZLM compatibility, still runs in foreground).

### Publish and play

```bash
# publish (RTMP / RTSP)
ffmpeg -re -i input.mp4 -c copy -f flv rtmp://127.0.0.1:1935/live/test
ffmpeg -re -i input.mp4 -c copy -f rtsp -rtsp_transport tcp rtsp://127.0.0.1:554/live/test

# play
ffplay rtmp://127.0.0.1:1935/live/test
ffplay -rtsp_transport tcp rtsp://127.0.0.1:554/live/test
ffplay http://127.0.0.1:8080/live/test.live.flv      # HTTP-FLV
# WebSocket-FLV: same path, upgrade the request to a WebSocket
```

HTTP-FLV / WS-FLV / RTSP paths accept `[vhost/]app/stream` with an optional leading vhost segment; RTMP uses `app/stream` in `connect` (stream keys default to `__defaultVhost__`).

### Using the ffmpeg dlls (in-process libav)

The default `zlmsharp.exe` spawns one `ffmpeg.exe` per capture/transcode. To run **FFmpeg.AutoGen 8.x against the ffmpeg dlls** in-process (no child process, no pipes), switch hosts: use `zlmsharp-libav.exe`.

- Only `zlmsharp-libav.exe` can use the dlls; `zlmsharp.exe` is a NativeAOT build without FFmpeg.AutoGen and cannot be switched.
- Everything above the media layer is identical: same flags, `zlmsharp.ini`, `.env`, logging, ports, APIs and AI analysis.

#### 1. Prepare the dlls

Drop the dlls from an ffmpeg **8.x shared build** (matching `FFmpeg.AutoGen 8.1.0`) **next to `ffmpeg.exe`**:

```
avcodec-62.dll  avformat-62.dll  avutil-60.dll  avfilter-11.dll
avdevice-62.dll swscale-9.dll    swresample-6.dll
```

The measured minimum is the first five (`avcodec` / `avformat` / `avutil` / `swscale` / `swresample`) — enough for frame grab, decode and libx264 push; `avfilter` / `avdevice` are not used by the current pipeline, so they can be omitted, or just copy the whole shared build folder (exe included). They must be **x64 and match the process bitness**, and the official shared builds need the MSVC runtime (`vcruntime140.dll` etc., i.e. the VC++ Redistributable).

#### 2. Point the host at the dll folder

The host takes the **folder of `ZLM_FFMPEG_BIN`** as the dll directory, so all of these work:

```dotenv
# .env — recommended: absolute path, shared by both hosts
# the exe host uses it as ffmpeg.exe; the libav host only uses its folder
ZLM_FFMPEG_BIN=E:\program\ffmpeg\81\ffmpeg.exe
```

```dotenv
# a directory also works (libav only — the exe host would fail to start)
ZLM_FFMPEG_BIN=E:\program\ffmpeg\81
```

- Process environment overrides `.env`: `$env:ZLM_FFMPEG_BIN='E:\program\ffmpeg\81\ffmpeg.exe'`.
- Relative paths resolve against the **current working directory** (not the exe folder), so always use an absolute path.
- When empty, the dll folder falls back to the current working directory: starting via `dotnet run` from the repo root, or the exe from another folder, will not find the dlls — **do not rely on the empty value**.
- Programmatic setup (your own host / embedding) skips `.env` entirely:

```csharp
// A: explicit ffmpeg.exe path or dll folder (internally replaces the default FfmpegToolkit)
builder.Services.AddAutoGenMediaToolkit(@"E:\program\ffmpeg\81");

// B: embedded host — register the defaults first, then swap the implementation
builder.Services.AddZlmSharp(options => options.RegisterStreamListeners = true);
builder.Services.AddZlmSharpExtra();
builder.Services.AddAutoGenMediaToolkit();
```

(`zlmsharp-libav` itself uses `CliApplication.ConfigureMedia`, an `internal` hook only reachable from projects that link `CliApplication.cs` the way it does.)

#### 3. Run

```bash
dotnet run --project src/ZlmSharp.Cli.Libav        # development
dotnet publish src/ZlmSharp.Cli.Libav -c Release   # publish (JIT, cannot be AOT)
# product: zlmsharp-libav.exe — same config and flags as zlmsharp.exe
```

`.env` is copied from `src/ZlmSharp.Cli/.env` into the output folder (see the `None Include` in the csproj), so editing that one file covers both hosts; after publishing you can also edit the `.env` next to the exe.

#### 4. Verify

1. Publish/start a stream, then trigger one capture or overlay (`startOverlay`, `analyzeDetect?analyzer=ocr`).
2. `Get-Process ffmpeg -ErrorAction SilentlyContinue` must be empty while overlay push, snapshots and recording still work — that means the dll path is in use.
3. With a wrong dll folder, the first media call throws `加载 ffmpeg dll 失败（目录 …）` and the log shows the directory it tried.

#### 5. Differences

| | `zlmsharp.exe` (default) | `zlmsharp-libav.exe` (dll) |
| --- | --- | --- |
| Media process | one `ffmpeg.exe` per capture/transcode | no child process, dlls in-process |
| Dependency | `ffmpeg.exe` from PATH or `ZLM_FFMPEG_BIN` | ffmpeg 8.x dlls in the same folder |
| Publish | NativeAOT, single file | JIT, needs the .NET runtime and the dlls |
| `FfprobePath` | configured value (nothing calls it today) | always empty |
| Exit code | from the ffmpeg child process | from the encoder thread |
| Behaviour | single frame grab, always-on sampling, sequential decode, libx264 overlay push | identical |

`samples` and `tests` keep using the default exe implementation.

## zlmsharp.exe: media backend, CLI and full configuration

### Which backend, which version

`src/ZlmSharp.Cli` (product `zlmsharp.exe`) is the default host; the current version is **0.1.4** (`-v` prints `zlmsharp 0.1.4 (build ..., branch/commit)`; the number comes from `Version` in `Directory.Build.props`, trust the actual `-v` output).

All media work sits behind `IMediaToolkit`, whose default implementation is `FfmpegToolkit`:

- **Child process + pipes** — every single-frame grab, always-on sampling, sequential decode and overlay push starts an `ffmpeg.exe` and exchanges PPM or raw RGB24 over stdin/stdout. No ffmpeg dll is loaded in-process and FFmpeg.AutoGen is not referenced.
- **ffmpeg.exe version** — not validated in code; only `-f image2pipe -vcodec ppm` (grab), `-f rawvideo -pix_fmt rgb24` (decode), `libx264` + `-f flv` (overlay push) are used. Every official build from 4.x to 8.x has them; this repo is tested with **8.x** (`E:\program\ffmpeg\81\ffmpeg.exe`). Custom builds must keep libx264 plus ppm / rawvideo / flv.
- **ffprobe** — `ZLM_FFPROBE_BIN` is parsed into `IMediaToolkit.FfprobePath`, but nothing calls it in the current version, so it is optional.
- **In-process variant** — `src/ZlmSharp.Cli.Libav` (`zlmsharp-libav.exe`) does the same with FFmpeg.AutoGen 8.1.0 + ffmpeg 8.x dlls; dll placement and `ZLM_FFMPEG_BIN` setup are in "Using the ffmpeg dlls (in-process libav)" above. Only the upper layers are shared.

The upper layers (RTSP / RTMP / HTTP-FLV / recording / AI analysis) depend on the interface only, so a different implementation can be registered wholesale:

```csharp
builder.Services.AddSingleton<IMediaToolkit>(provider =>
    new MyToolkit(provider.GetRequiredService<AppOptions>().Env));
```

### CLI and exit codes

```bash
zlmsharp                                    # reads zlmsharp.ini + .env next to the exe
zlmsharp -c ./conf/zlmsharp.ini -e ./conf/.env
zlmsharp -v                                 # version + build time + branch/commit
zlmsharp -h                                 # usage
```

| Flag | Meaning |
| --- | --- |
| `-c`, `--conf <path>` | Explicit `zlmsharp.ini`. A **missing file is a hard error** (it is not generated) |
| `-e`, `--env <path>` | Explicit `.env`, same rule |
| `-d`, `--daemon` | Accepted for ZLM script compatibility only; still runs in the foreground and just logs a line |
| `-v`, `--version` | Print version and exit |
| `-h`, `--help`, `-?` | Print usage and exit |
| anything else | Error + usage, exit code 2 |

| Exit code | Meaning |
| --- | --- |
| 0 | Clean shutdown (Ctrl+C / SIGTERM) |
| 1 | Config load failure, host init failure, runtime exception |
| 2 | Bad command line |
| 3 | HTTP port cannot be bound (in use, or non-admin Windows below 1024) |

### First run and path rules

The first start writes two files next to the executable (`AppContext.BaseDirectory`, i.e. `bin/Debug/net8.0/` or the publish folder) and prints a notice:

- `zlmsharp.ini` — business config, tables below; the comments and defaults are the built-in template.
- `.env` — machine-specific / secret / external-tool paths (ffmpeg, models, record root).

Every relative path in the ini (`./www`, `./log`, `./WebUI`, `./www/snap/`) is resolved against the **exe folder**, not the current working directory — including when `-c` points somewhere else.

### Precedence and syntax

```
process environment  >  .env  >  zlmsharp.ini  >  built-in defaults
```

- The ini is overlaid on top of the built-in defaults, so keys added by an upgrade are never missing; unknown keys are ignored, out-of-range values are clamped/reset with a startup warning (e.g. `[http] port=70000`).
- Supports `[section]`, `key=value`, full-line `;` / `#` comments; later duplicate keys win; key names are case-insensitive.
- Booleans accept `0/1` (also `true/false`, `on/off`, `yes/no`).
- Port `0` disables that protocol (HTTP=0 keeps RTSP/RTMP only).
- `getServerConfig` / `setServerConfig` are limited to the `ConfigSchema` whitelist; rows marked "ini only" (e.g. `api.pageToken`) can only be set in the file.

### `[api]`

| Key | Default | Meaning |
| --- | --- | --- |
| `apiDebug` | `0` | Log every HTTP API request/response |
| `secret` | `035c73f7-...` | `secret` for sensitive APIs; requests from `127.0.0.1` / `::1` bypass it, empty disables the check |
| `pageToken` | empty (ini only) | Token for `/zlm/admin`, see "Admin page access" |
| `snapRoot` | `./www/snap/` | Snapshot root (reserved) |
| `defaultSnap` | `./www/logo.png` | Default snapshot image (reserved) |
| `downloadRoot` | `./www` | Root exposed by `downloadFile` |

### `[general]`

| Key | Default | Meaning |
| --- | --- | --- |
| `enableVhost` | `0` | Enable virtual hosts (stream keys then carry a vhost) |
| `flowThreshold` | `1024` | Traffic-event threshold (KB) |
| `maxStreamWaitMS` | `15000` | How long a player waits for the stream to register (ms) |
| `streamNoneReaderDelayMS` | `20000` | Delay before closing a stream nobody watches (ms) |
| `resetWhenRePlay` | `1` | Reset timestamps when publishing again after idle |
| `mergeWriteMS` | `0` | Merged-write window (ms, 0 = off) |
| `gop_cache_ms` | `0` | GOP replay window for new viewers (ms); 0 = full GOP (instant start, latency ≈ camera GOP), e.g. 1000 lowers latency |
| `catch_up_pending_frames` | `0` | Drop backlog and jump to the newest keyframe past this many queued frames (0 = off, 150~300 recommended) |
| `mediaServerId` | `your_server_id` | Server id |
| `listen_ip` | `::` | Listen address(es), comma separated; `::` / `0.0.0.0` = all |
| `wait_track_ready_ms` | `10000` | Track-ready timeout (ms) |
| `wait_audio_track_data_ms` | `1000` | First-audio-data timeout (ms) |
| `wait_add_track_ms` | `3000` | addTrack timeout (ms) |
| `unready_frame_cache` | `100` | Frames cached before the track is ready |
| `broadcast_player_count_changed` | `0` | Broadcast viewer-count changes |

### `[http]`

| Key | Default | Meaning |
| --- | --- | --- |
| `port` | `8080` | HTTP/API and HTTP-FLV port; `0` = off. ZLM uses 80; 8080 avoids the non-admin limit on Windows |
| `sslport` | `443` | HTTPS port (reserved) |
| `rootPath` | `./WebUI` | Static site folder (the admin page is embedded) |
| `charSet` | `utf-8` | Default charset |
| `keepAliveSecond` | `30` | HTTP keep-alive (s) |
| `maxReqSize` | `40960` | Request-header limit (bytes) |
| `maxUploadSize` | `1073741824` | Upload limit (bytes, 1 GiB) |
| `sendBufSize` | `65536` | Socket send buffer (bytes) |
| `dirMenu` | `1` | Directory listing |
| `virtualPath` | empty | Virtual path prefix |
| `forbidCacheSuffix` | empty | Suffixes that must not be cached |
| `allow_origins` | `*` | CORS origins |
| `allow_ip_range` | private ranges (below) | Allowed IPs/CIDRs, `a-b` ranges supported; default is `::1,127.0.0.1,172.16.0.0-172.31.255.255,192.168.0.0-192.168.255.255,10.0.0.0-10.255.255.255`, empty = unrestricted |
| `forwarded_ip_header` | empty | Header used for the real client IP (behind a proxy) |

### `[rtsp]`

| Key | Default | Meaning |
| --- | --- | --- |
| `port` | `554` | RTSP port; `0` = disabled; use `8554` without admin rights |
| `sslport` | `0` | RTSPS port (0 = off) |
| `authBasic` | `0` | Enable Basic auth |
| `directProxy` | `1` | Pull proxy passes media through untouched |
| `handshakeSecond` | `15` | Handshake timeout (s) |
| `keepAliveSecond` | `15` | Keep-alive timeout (s) |
| `lowLatency` | `0` | Low-latency mode |
| `rtpTransportType` | `-1` | RTP transport: -1 auto, 0 UDP, 1 TCP |

### `[rtmp]`

| Key | Default | Meaning |
| --- | --- | --- |
| `port` | `1935` | RTMP port; `0` = disabled |
| `sslport` | `0` | RTMPS port (0 = off) |
| `directProxy` | `1` | Pull proxy passes media through untouched |
| `handshakeSecond` | `15` | Handshake timeout (s) |
| `keepAliveSecond` | `15` | Keep-alive timeout (s) |
| `enhanced` | `1` | Enhanced RTMP support (`codecId=12` / `hvc1` HEVC-in-FLV headers) |

### `[protocol]`

| Key | Default | Meaning |
| --- | --- | --- |
| `modify_stamp` | `2` | Timestamp rewrite: 0 none, 1 first frame, 2 all |
| `enable_audio` | `1` | Keep audio in transcoded outputs |
| `add_mute_audio` | `1` | Add silent AAC when the source is video-only |
| `auto_close` | `0` | Close streams with no viewers immediately (otherwise `streamNoneReaderDelayMS`) |
| `continue_push_ms` | `15000` | Grace period after a publisher drops (ms) |
| `paced_sender_ms` | `0` | Paced-send interval (ms, 0 = off) |
| `enable_hls` | `0` | Generate HLS |
| `enable_hls_fmp4` | `0` | Generate HLS-fMP4 |
| `enable_mp4` | `0` | Record MP4 |
| `enable_flv_record` | `0` | Also keep raw FLV recordings (MP4 is the official one) |
| `enable_rtsp` | `1` | Generate RTSP output |
| `enable_rtmp` | `1` | Generate RTMP/FLV output |
| `enable_ts` | `0` | Generate TS |
| `enable_fmp4` | `0` | Generate fMP4 |
| `hls_demand` / `rtsp_demand` / `rtmp_demand` / `ts_demand` / `fmp4_demand` | `0` | Generate that protocol on demand (only while watched) |
| `mp4_max_second` | `3600` | MP4 segment length (s) |
| `mp4_save_path` | `./www` | Recording root (overridable via `ZLM_RECORD_ROOT`) |
| `hls_save_path` | `./www` | HLS output folder |

### `[proxy]` (pull proxy)

| Key | Default | Meaning |
| --- | --- | --- |
| `enable` | `0` | Enable the startup list |
| `max_concurrency` | `16` | Concurrent pulls (clamped 1~256) |
| `retry_delay_ms` | `2000` | Reconnect delay (ms, clamped 200~60000) |
| `list` | empty | Pull on startup: `app/stream=url`, `;`-separated, `rtmp://` / `rtsp://` |
| `keep_on_none_reader` | `0` (not in the template, add manually) | Keep startup-list proxies alive with no viewers; `addStreamProxy?keep=1` overrides per stream |
| `on_demand_list` | empty | On-demand list: connect only when someone plays |
| `on_demand_idle_ms` | `10000` | Hold time after the last viewer leaves (ms) |
| `on_demand_max_wait_ms` | `30000` | Stop if nobody watched within this window (ms), should exceed `maxStreamWaitMS` |

### `[hls]`

| Key | Default | Meaning |
| --- | --- | --- |
| `seg_duration` | `5` | Segment length (s, keyframe-aligned, clamped 1~60) |
| `seg_num` | `5` | Segments kept in the playlist (clamped 2~100) |

### `[hook]` (media event callbacks)

| Key | Default | Meaning |
| --- | --- | --- |
| `enable` | `0` | Enable hooks |
| `on_publish` | empty | Publish auth: POST JSON, `{"code":0}` allows |
| `on_play` | empty | Play auth, same contract |
| `on_stream_none_reader` | empty | Called when the last viewer leaves; 0 allows closing |
| `timeoutSec` | `10` | Per-request timeout (s) |
| `retry` | `0` | Retries on failure |
| `retry_delay` | `1000` | Retry delay (ms) |
| `fail_open` | `0` | Allow when the hook is unreachable; default deny so auth cannot be bypassed |

### `[log]` (zlmsharp extension)

| Key | Default | Meaning |
| --- | --- | --- |
| `level` | `2` | 0 trace, 1 debug, 2 info, 3 warn, 4 error, 5 critical, 6 off; out-of-range resets to 2 |
| `console` | `1` | Log to console |
| `file` | `./log/zlmsharp.log` | Log file; empty disables file logging |
| `maxFileSizeMB` | `20` | Per-file limit (MB) |
| `maxFiles` | `5` | Files kept |

### All `.env` keys

`.env` holds machine-specific / secret / external-tool paths; key names are case-insensitive and `#` comments, an `export` prefix, quotes and `${NAME}` expansion are supported. **Process environment wins over the file**, and file values are injected into the process environment (so ffmpeg children see them too).

| Key | Default | Meaning |
| --- | --- | --- |
| `ZLM_FFMPEG_BIN` | empty (PATH `ffmpeg`) | Absolute ffmpeg.exe path; `zlmsharp-libav` uses its **folder** as the dll directory (a directory works too, libav only). Details in "Using the ffmpeg dlls (in-process libav)" |
| `ZLM_FFPROBE_BIN` | empty (PATH `ffprobe`) | Not called by the current version |
| `ZLM_RECORD_ROOT` | empty (`protocol.mp4_save_path`) | Recording root, overrides the ini |
| `ZLM_EXTRA_ENABLE` | `1` | AI extension master switch: 0 disables scheduled analysis and overlays (single-image APIs keep working) |
| `ZLM_ANALYSIS_SAMPLE_INTERVAL_MS` | falls back to `ZLM_YOLO_SAMPLE_INTERVAL_MS` (1000) | Global sampling interval (ms) |
| `ZLM_ANALYSIS_ENABLED` | empty = all registered | Analyzer whitelist, comma separated, e.g. `yolo,ocr` |
| `ZLM_ANALYSIS_BUSY_POLICY` | `skip` | When an analyzer is busy: `skip` drops the round, `queue` waits (latency accumulates) |
| `ZLM_YOLO_MODEL_DIR` | empty | Model folder; used when `ZLM_YOLO_MODEL` is a bare file name |
| `ZLM_YOLO_MODEL` | empty = YOLO off | Model path (`.pt`/`.onnx`); relative paths resolve against the exe folder |
| `ZLM_YOLO_ENDPOINT` | empty | Reserved: external inference endpoint (local SimdYolo is used) |
| `ZLM_YOLO_DEVICE` | `cpu` | `cpu` / `vulkan` / `metal` / `auto` |
| `ZLM_YOLO_CONFIDENCE` | `0.25` | Confidence threshold (0~1) |
| `ZLM_YOLO_INPUT_WIDTH` / `ZLM_YOLO_INPUT_HEIGHT` | `640` / `640` | Model input size (`.pt` needs multiples of 32) |
| `ZLM_YOLO_CPU_THREADS` | `0` | CPU thread cap, 0 = library default |
| `ZLM_YOLO_SAMPLE_INTERVAL_MS` | `1000` | Minimum interval between two detections per stream (ms), 0 = unlimited |
| `ZLM_OCR_ENABLE` | `0` | OCR switch (CPU heavy, off by default) |
| `ZLM_OCR_MODEL` | `tiny` | Built-in model (PP-OCRv6 tiny); ignored when custom models are complete |
| `ZLM_OCR_DET_MODEL` / `ZLM_OCR_REC_MODEL` / `ZLM_OCR_DICT` | empty | Custom det/rec models + dictionary; all three enable them |
| `ZLM_OCR_CLS_MODEL` | empty | Optional direction classifier |
| `ZLM_OCR_MIN_SCORE` | `0.3` | Minimum recognition score (0~1) |
| `ZLM_OCR_USE_CLS` | `1` | Use the classifier to fix upside-down text |
| `ZLM_OCR_LINE_WORKERS` | `1` | Parallel text-line workers (clamped 1~64) |
| `ZLM_OCR_SAMPLE_INTERVAL_MS` | `2000` | Minimum interval between two OCR passes per stream (ms) |
| `ZLM_OVERLAY_SUFFIX` | `_ai` | Derived overlay stream suffix (`live/test` → `live/test_ai`) |
| `ZLM_OVERLAY_FRAME_POLICY` | `reuse` | `reuse` draws the latest result on every frame (no frame drops); `skip` only draws fresh results |
| `ZLM_OVERLAY_RESULT_MAX_AGE_MS` | `0` | Max age of a reusable result (ms); 0 = policy default (reuse 5000 / skip 200) |

### Minimal working config

```ini
[general]
mediaServerId=demo-01
listen_ip=0.0.0.0
[http]
port=8080
[rtsp]
port=8554          ; bindable without admin rights
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
# .env: machine-specific values only, business config stays in the ini
ZLM_FFMPEG_BIN=E:\program\ffmpeg\81\ffmpeg.exe
ZLM_RECORD_ROOT=D:\records
```

## Configuration

Per-key meaning, defaults, valid values and the complete `.env` list live in "zlmsharp.exe: media backend, CLI and full configuration" above; this section only covers how the two files are generated and loaded.

Two files, both optional and auto-generated on first run:

- `zlmsharp.ini` — business config, section/key names aligned with ZLM's `conf/config.ini` (`[api]`, `[general]`, `[http]`, `[rtsp]`, `[rtmp]`, `[protocol]`, `[hook]`, `[hls]`), plus zlmsharp extensions `[proxy]` (pull proxy) and `[log]` (NLog).
- `.env` — machine-specific / sensitive values: ffmpeg paths, record root, YOLO model dir, etc. Process environment variables win over `.env`.

The ini parser understands `[section]`, `key=value`, whole-line `;` / `#` comments and last-key-wins; your file is overlaid on the built-in defaults, so upgrading never leaves new keys unset. Out-of-range values are clamped with a startup warning, and unrecognized keys are simply ignored.

### Admin page access

The built-in console is served at `/zlm/admin` (single-file HTML embedded in the assembly). Access control is opt-in:

- `[api] pageToken=...` — when set, the page requires `?token=...` or the `X-ZlmSharp-Token` header; a successful check stores an `HttpOnly` cookie for 7 days, so refreshes and direct visits no longer need the token. Empty (default) means the page is public.
- Custom policy (SSO, IP allow-list, OTP, …) — register `IAdminPageAuthorizer` after `AddZlmSharp` and it replaces the default token check entirely:

```csharp
builder.Services.AddSingleton<IAdminPageAuthorizer, MyAdminPageAuthorizer>();
```

## HTTP API

All responses follow ZLM's `{"code":0,"msg":"success","data":...}` envelope. `GET /index/api/getApiList` returns the implemented set. Calls from non-loopback addresses must pass `secret=` matching `[api] secret` (excluding the key entirely disables the check); requests from `127.0.0.1` / `::1` are exempt, mirroring ZLM.

| Endpoint | Purpose |
| --- | --- |
| `version`, `getApiList` | Build info, supported API list |
| `getMediaList` | Online streams: codec, resolution, fps, reader count, uptime, `originType`, `dropFrames`; filters `vhost`/`app`/`stream`/`schema` |
| `close_streams` | Close matching streams, returns `count` / `closed`; managed streams are skipped — pull proxies only stop via `delStreamProxy`, overlay outputs only via `stopOverlay` |
| `getSessionList`, `kick_session` | Inspect or kick publish/play sessions (by `id=` or filters) |
| `startRecord`, `stopRecord`, `isRecording`, `getRecordList` | Recording control and task list: `type=1` MP4, `2` FLV, `3` MP4+FLV, `4` HLS, `5` all |
| `getMp4RecordFile`, `deleteRecordFile` | Browse / delete recorded files (`app`+`stream`+`period`+`path`), path-escape safe |
| `GET /record/{relative path}` | Play / download a recorded file (Range supported); path is relative to the record root, no secret required |
| `startPush`, `stopPush`, `getPushList` | RTMP relay push |
| `addStreamProxy`, `delStreamProxy`, `getProxyList` | Pull proxy (RTMP/RTSP source → local stream), list includes source URL, online state, `keepOnNoneReader` and `onDemand`; by default an idle proxy is reclaimed like any other stream, pass `keep=1` to keep pulling forever until `delStreamProxy` (`[proxy] keep_on_none_reader` sets the default for the startup list); `on_demand=1` only registers an on-demand source (pulled when someone plays it) |
| `yoloInfo`, `yoloDetect`, `getYoloResult` | YOLO model info, one-shot detection, latest periodic result (compat endpoints backed by the analyzer layer) |
| `getAnalyzerList`, `analyzeDetect`, `analyzeResult` | Analyzer catalog, one-shot analysis for any analyzer (`analyzer=`), latest periodic results |
| `startOverlay`, `stopOverlay`, `getOverlay` | Generic overlay: draw any analyzer's results onto a derived `{stream}_ai` output stream |
| `startYoloOverlay`, `stopYoloOverlay`, `getYoloOverlay` | YOLO-flavored overlay endpoints (compat) |

## Pull proxy

```bash
# pull a remote stream into local live/tencent
curl "http://127.0.0.1:8080/index/api/addStreamProxy?app=live&stream=tencent&url=rtmp%3A%2F%2Fexample.com%2Flive%2Fdemo"
curl "http://127.0.0.1:8080/index/api/delStreamProxy?key=__defaultVhost__%2Flive%2Ftencent"
```

- Sources: `rtmp://` or `rtsp://` (RTSP uses TCP interleaved; `rtsp://user:pass@host/...` sends `Authorization: Basic`).
- Local stream reports `originType=proxy`, and `schema` follows the source protocol.
- Reconnects automatically until stopped; proxied streams can be played, recorded, relayed and YOLO-analyzed like any local stream.
- Start-on-boot list:

```ini
[proxy]
enable=1
max_concurrency=16   # refuses new pulls beyond this limit
retry_delay_ms=2000
list=live/cam1=rtsp://192.168.1.10:554/stream1; live/cam2=rtmp://example.com/live/demo
```

### On-demand pull (only while someone watches)

Declare the sources up front; nothing is pulled until a player asks for the stream. When the last viewer
leaves the proxy is stopped, and the next play request pulls it again. If the source drops while viewers
are still connected, the proxy keeps retrying every `retry_delay_ms`.

```ini
[proxy]
on_demand_list=live/cam1=rtsp://192.168.1.10:554/stream1; live/cam2=rtmp://example.com/live/demo
on_demand_idle_ms=10000      # keep pulling this long after the last viewer left (absorbs page refreshes)
on_demand_max_wait_ms=30000  # give up if nobody ever watches (source down / player already gone)
```

- Triggers from HTTP-FLV, WS-FLV, RTMP `play` and RTSP `DESCRIBE`/`PLAY`.
- On-demand streams are not reclaimed by `auto_close` / `on_stream_none_reader`; the viewer count decides.
- AI sampling skips them (its decoder counts as a viewer and would keep the proxy alive); use `list` if you need analysis.

Sources can also be registered at runtime:

```bash
# register only (on_demand=1), nothing is pulled yet
curl "http://127.0.0.1:8080/index/api/addStreamProxy?app=live&stream=cam3&on_demand=1&url=rtsp%3A%2F%2F192.168.1.11%3A554%2Fstream1"
# listing shows pending entries with onDemand=true, online=false
curl "http://127.0.0.1:8080/index/api/getProxyList"
# remove the registration (same endpoint as a normal proxy)
curl "http://127.0.0.1:8080/index/api/delStreamProxy?key=__defaultVhost__%2Flive%2Fcam3"
```

```csharp
// embedded: inject IOnDemandProxy and manage entries in code
onDemandProxy.Register(StreamKey.Create(null, "live", "cam3"), "rtsp://192.168.1.11:554/stream1");
onDemandProxy.Unregister(StreamKey.Create(null, "live", "cam3"));
```

## Recording

- `startRecord?app=&stream=&type=&max_second=` starts recording an online stream; files land in `ZLM_RECORD_ROOT` (fallback `protocol.mp4_save_path`) under `app/stream/yyyy-MM-dd/`.
- `mp4Path` / `flvPath` / `hlsPath` in API responses are **normalized paths relative to the record root** (`/` separated, no drive letter or prefix, e.g. `live/cam/2026-11-04/20-15-08-001.mp4`); append to `http://host:port/record/` to play or download (same for `rootPath` from `getMp4RecordFile`).
- MP4 samples use AVCC (`avcC` + `esds`), FLV is append-only (survives crashes), HLS writes `hls-000001.ts…` + `hls.m3u8` aligned on key frames (`[hls] seg_duration`, `seg_num`).
- Recording resumes automatically after a publisher reconnectes; `protocol.enable_mp4` / `enable_flv_record` enable always-on recording at startup (`*_ai` overlay streams are skipped).

## AI analysis (optional, ZlmSharp.Extra)

The core library keeps only contracts and HTTP entry points. Frame capture (ffmpeg), inference, tracking, drawing and the overlay pipeline live in `ZlmSharp.Extra`, which hosts opt into explicitly; without it every AI endpoint answers "not enabled".

```csharp
builder.Services.AddZlmSharp(...);          // core: protocols + API, zero AI dependencies
builder.Services.AddZlmSharpExtra(extra =>  // optional: built-in YOLO + sampling scheduler + overlay pipeline
{
    extra.AddAnalyzer<MyOcrAnalyzer>(o => o.Outputs = AnalysisOutputs.None); // analyze only, ignore results
});
```

Built-in YOLO is configured entirely through `.env`: `ZLM_YOLO_MODEL_DIR`, `ZLM_YOLO_MODEL`, `ZLM_YOLO_DEVICE` (`cpu`/`vulkan`/`metal`/`auto`), `ZLM_YOLO_CONFIDENCE`, input size, thread count, sample interval. Models are loaded lazily and cached (LRU); inference runs on a resident ffmpeg decoder frame, and results are kept per stream with stable IoU `trackId`s. `startYoloOverlay` / `startOverlay` spawn a decode → draw → encode pipeline that publishes `{stream}_ai` back into the server.

Third-party algorithms plug in without touching the core (see `extra-design.md`):

- **L1 (recommended)**: implement `IStreamAnalyzer` (plus optional `IOverlayRenderer` / `IAnalysisSink`) — you get `FrameSample` (RGB24) and return `AnalysisResult` primitives; no ffmpeg/HTTP knowledge required.
- **L2 (inference kernel)**: implement `IYoloBackendFactory` / `IYoloInferenceBackend` to reuse the built-in YOLO pre/post-processing and model cache with a different inference kernel (e.g. ONNX Runtime / vendor SDK adapters).
- Result routing: `Snapshot` (queryable via `analyzeResult`), `Snapshot|Overlay` (derived `{stream}_ai` stream), `None` (analysis only — the algorithm consumes its own results). Directory-scan plugin loading is not implemented yet; third parties register at compile time via `AddAnalyzer` (AOT friendly).

Common configuration: `ZLM_EXTRA_ENABLE`, `ZLM_ANALYSIS_SAMPLE_INTERVAL_MS`, `ZLM_ANALYSIS_ENABLED` (whitelist), `ZLM_OVERLAY_SUFFIX` (default `_ai`).

## Hooks and stability

```ini
[hook]
enable=1
on_publish=http://127.0.0.1:9000/on_publish
on_play=http://127.0.0.1:9000/on_play
on_stream_none_reader=http://127.0.0.1:9000/on_none_reader
timeoutSec=10
fail_open=0     # 0 = deny when the hook is unreachable
```

Hook payloads mirror ZLM (`mediaServerId`, `app`, `stream`, `vhost`, `schema`, `id`, `ip`, `port`, `user_agent`, `params`); query parameters of the publish/play URL are forwarded in `params`, so token-based auth is decided externally. Idle streams are reclaimed after `general.streamNoneReaderDelayMS` according to `protocol.auto_close` / `on_stream_none_reader`; streams being recorded and overlay outputs are never reclaimed this way. Pull proxies follow the same rule unless created with `keep=1` (or `[proxy] keep_on_none_reader=1`), which keeps them pulling until `delStreamProxy`.

## Embedding

```csharp
builder.Services.AddZlmSharp(o => o.Options.Http.Port = 8080);
var app = builder.Build();
app.UseZlmSharp();
```

`samples/ZlmSharp.Sample.Web` demonstrates embedding inside a normal ASP.NET Core app (listeners off by default, HTTP API on port 5180).

## Repository layout

```
src/ZlmSharp/           class library "ZlmSharp.MediaServer" (namespaces mirror ZLM structure)
  Core/ Codecs/ Configuration/ Protocols/{Flv,Rtmp,Rtsp}/ Http/ Recording/ Api/ Server/
  Ai/                    AI contracts (IStreamAnalyzer / IOverlayController / result models) + top-level calls
src/ZlmSharp.Extra/     AI implementation ("ZlmSharp.Extra"): ffmpeg capture, YOLO, overlay pipeline, sampling scheduler
src/ZlmSharp.Media.AutoGen/ optional media backend (FFmpeg.AutoGen 8.x): in-process ffmpeg dlls, no ffmpeg.exe
src/ZlmSharp.Cli/       executable entry point (product: zlmsharp.exe, NativeAOT)
src/ZlmSharp.Cli.Libav/ executable entry point (product: zlmsharp-libav.exe, in-process dll backend, JIT)
samples/…Sample.Web/    embedding sample (no Extra — AI endpoints degrade to "not enabled")
tests/ZlmSharp.Tests/   xUnit unit + end-to-end tests
assets/                 app.ico (multi-size) + app-icon.png, generated by tools/make-icon.ps1
```

## Build, publish, test

```bash
dotnet build zlmsharp.sln                       # treat warnings as errors, AOT analyzers on
dotnet test  tests/ZlmSharp.Tests               # 268 tests
dotnet publish src/ZlmSharp.Cli -c Release -r win-x64   # NativeAOT single exe
```

NativeAOT needs MSVC + Windows SDK on Windows (clang + zlib1g-dev on Linux). Publishing drops the trimmed exe next to a generated `zlmsharp.ini`; the Win32 icon comes from `assets/app.ico`.

A real-model YOLO smoke test (not part of the normal suite):

```bash
ZLM_YOLO_TEST_MODEL=<path-to.onnx> dotnet test --filter YoloModelSmokeTests
```

## Known limitations

- Video is H.264 / H.265; audio is AAC only. HEVC-in-FLV has two header formats (legacy `codecId=12` and Enhanced RTMP); see "Enhanced RTMP compatibility" below.
- RTSP pull proxy uses TCP interleaved only; RTSP push side sends RR but never SR.
- HLS is a recording format today — `protocol.enable_hls` is parsed but not wired to playback.
- MP4 files are only playable after recording stops (moov is written at the end); use FLV for crash tolerance.
- Single process, in-memory state: no clustering, no RTSP/RTMP SSL, no WebRTC.

## Enhanced RTMP compatibility (the two HEVC-in-FLV headers)

HEVC can be carried in FLV/RTMP in two mutually incompatible ways. This server handles **both**: it accepts either format on input, and picks one format on output.

### How the two formats differ

| Item | legacy (non-standard, common in the wild) | Enhanced RTMP (E-RTMP v1/v2) |
|---|---|---|
| Video tag first byte | `[FrameType:4][CodecId:4]`, HEVC always `codecId=12`: key `0x1C`, inter `0x2C` | `0x80 \| [FrameType:3][PacketType:4]`: sequence `0x90`, key `0x93`, inter `0xA1` |
| Codec identity | low nibble of the first byte = 12 | 4-byte FourCC `'hvc1'` right after the first byte |
| Config frame | 2nd byte `AVCPacketType=0` + `hvcC` | `PacketType=0` (SequenceStart) + `hvcC` |
| Coded frame | 2nd byte `AVCPacketType=1` + fixed 3-byte CTS + AVCC payload | `PacketType=1` (CodedFrames) with 3-byte CTS, or `PacketType=3` (CodedFramesX) without CTS (treated as 0) |
| `onMetaData.videocodecid` | `12` | `1752589105` (integer value of `'hvc1'`, matching FFmpeg 8.x / OBS) |

### Input (publish, pull proxy)

Both formats are detected **automatically, with no configuration**, by bit 7 of the video tag header, per tag. Legacy and enhanced publishers can be online at the same time.

- bit 7 = 0 and low nibble = 12: parsed as legacy (2nd byte is `AVCPacketType`, bytes 3-5 are CTS)
- bit 7 = 1: parsed as enhanced (FourCC + `PacketType`; `CodedFramesX` has no CTS, treated as 0)
- `PacketType=2/4/6/7` (SequenceEnd / Metadata / Multitrack / ModEx): ignored, not treated as a picture
- H.264 (low nibble = 7) is unchanged and stays on the legacy path

### Output (play, relay push, HTTP-FLV/WS-FLV)

An FLV tag can only carry one header, so output is **globally one or the other**, configured in `zlmsharp.ini`:

```ini
[rtmp]
; 1 = enhanced header for HEVC (default); 0 = fall back to legacy codecId=12
enhanced=1
```

- The switch applies to RTMP playback, RTMP relay push, HTTP-FLV and WS-FLV; it only affects new connections (existing streams do not renegotiate).
- **H.264 always stays legacy** (`0x17` / `0x27`) in both modes: the `avc1` enhanced header is only meant for multitrack, and legacy is the most compatible.
- `onMetaData.videocodecid` follows the mode: `1752589105` when enhanced, `12` when legacy.
- FLV recording is **always legacy**, so older players and ffmpeg tooling can read the files.

### Peer support

| Peer | Reads enhanced header | Reads legacy 12 | Notes |
|---|---|---|---|
| FFmpeg 8.x | ✅ | ✅ | Verified both ways with 8.1.2 |
| FFmpeg 7.1+ | ✅ | ✅ | Enhanced header support starts at 7.1 |
| FFmpeg ≤ 7.0 | ❌ | ✅ | Legacy only, set `enhanced=0` |
| Browser `mpegts.js` (admin page preview) | ❌ | ❌ | No HEVC-in-FLV support at all, regardless of header; use RTSP or an `hvc1`-capable player |

OBS 30+ already emits the enhanced header for HEVC/AV1, so it can publish here without extra configuration.

### Measurements

Local FFmpeg 8.1.2 (`ZLM_FFMPEG_TEST_PATH` pointing at that exe):

1. `ffmpeg -c:v libx265 -f flv` publish → this server parses H265 320x240 (enhanced input);
2. ffprobe pulling RTMP from this server: both `enhanced=1` and `enhanced=0` outputs are detected as `hevc`, width 320;
3. Cross-check against an FLV produced by local ffmpeg: same first byte `0x90` + `'hvc1'` and the same `videocodecid=1752589105` as our output.

### Not supported yet

- `PacketType=6` (Multitrack) and `7` (ModEx / nanosecond timestamps) are recognized but not used;
- AV1 / VP9 FourCCs (`av01` / `vp09`) are recognized but not decoded;
- No per-client header selection: it is a global switch. A per-request override such as `?enhanced=0` can be added later.

## Roadmap

Progress and detailed design notes live in `workplan.md` (kept out of git by `.gitignore`). Next phase:

1. **H.265 / HEVC end to end** — VPS/SPS/PPS parsing, RFC 7798 RTP, FLV `hvcC`, MP4 `hvc1`, TS `0x24`, RTSP/RTMP in/out and relay.
2. **Web admin page** — built-in static site (no frontend build step) over the existing `/index/api/*`: streams, sessions, recording, proxy/relay, AI results.
3. **Generic AI layer** — split frame source / `IStreamAnalyzer` / `IOverlayRenderer`, so YOLO becomes one plugin and OCR, license-plate or zone-intrusion analyzers can be added later.
4. **GB/T 28181 and JT/T 809** — SIP + PS-over-RTP camera onboarding, and vehicle registration/position/alarm ingestion.

## License

GPL-3.0 — see [LICENSE](LICENSE).
