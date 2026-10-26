# zlmsharp

<img src="assets/app-icon.png" width="96" align="right" alt="zlmsharp" />

A C#/.NET 10 port of the [ZLMediaKit](https://github.com/ZLMediaKit/ZLMediaKit) streaming core: ingest, media source fan-out, protocol conversion and a ZLM-compatible HTTP API — as a single small binary plus one class library.

**English** | [简体中文](README.cn.md)

- RTMP publish / play, RTSP publish / play (TCP interleaved + UDP), HTTP-FLV, WebSocket-FLV
- One `MediaSource` per stream: ring buffer, GOP snapshot, frame-level fan-out with per-reader drop accounting
- MP4 / FLV / HLS(TS) recording, RTMP relay push, RTSP/RTMP pull proxy
- Hooks (`on_publish` / `on_play` / `on_stream_none_reader`), session registry, idle-stream reclamation
- Optional YOLO detection / overlay on top of any live stream (pure managed inference, no native deps)
- NativeAOT publish friendly; no reflection-heavy configuration binding

---

## Quick start

Requirements: .NET 10 SDK (`global.json` pins `10.0.x`).

```bash
git clone <repo> zlmsharp && cd zlmsharp
dotnet build zlmsharp.sln
dotnet run --project src/ZlmSharp.Cli
```

First start writes `zlmsharp.ini` and `.env` next to the executable (`src/ZlmSharp.Cli/bin/Debug/net10.0/`), then listens on:

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

## Configuration

Two files, both optional and auto-generated on first run:

- `zlmsharp.ini` — business config, section/key names aligned with ZLM's `conf/config.ini` (`[api]`, `[general]`, `[http]`, `[rtsp]`, `[rtmp]`, `[protocol]`, `[hook]`, `[hls]`), plus zlmsharp extensions `[proxy]` (pull proxy) and `[log]` (NLog).
- `.env` — machine-specific / sensitive values: ffmpeg paths, record root, YOLO model dir, etc. Process environment variables win over `.env`.

The ini parser understands `[section]`, `key=value`, whole-line `;` / `#` comments and last-key-wins; your file is overlaid on the built-in defaults, so upgrading never leaves new keys unset. Out-of-range values are clamped with a startup warning, and unrecognized keys are simply ignored.

## HTTP API

All responses follow ZLM's `{"code":0,"msg":"success","data":...}` envelope. `GET /index/api/getApiList` returns the implemented set. Calls from non-loopback addresses must pass `secret=` matching `[api] secret` (excluding the key entirely disables the check); requests from `127.0.0.1` / `::1` are exempt, mirroring ZLM.

| Endpoint | Purpose |
| --- | --- |
| `version`, `getApiList` | Build info, supported API list |
| `getMediaList` | Online streams: codec, resolution, fps, reader count, uptime, `originType`, `dropFrames`; filters `vhost`/`app`/`stream`/`schema` |
| `close_streams` | Close matching streams, returns `count` / `closed` |
| `getSessionList`, `kick_session` | Inspect or kick publish/play sessions (by `id=` or filters) |
| `startRecord`, `stopRecord`, `isRecording` | Recording control: `type=1` MP4, `2` FLV, `3` MP4+FLV, `4` HLS, `5` all |
| `getMp4RecordFile`, `deleteRecordFile` | List / delete recorded files (`app`+`stream`+`period`+`path`), path-escape safe |
| `startPush`, `stopPush`, `getPushList` | RTMP relay push |
| `addStreamProxy`, `delStreamProxy` | Pull proxy (RTMP/RTSP source → local stream) |
| `yoloInfo`, `yoloDetect`, `getYoloResult` | YOLO model info, one-shot detection, latest periodic result |
| `startYoloOverlay`, `stopYoloOverlay`, `getYoloOverlay` | Draw boxes onto a derived `{stream}_yolo` output stream |

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

## Recording

- `startRecord?app=&stream=&type=&max_second=` starts recording an online stream; files land in `ZLM_RECORD_ROOT` (fallback `protocol.mp4_save_path`) under `app/stream/yyyy-MM-dd/`.
- MP4 samples use AVCC (`avcC` + `esds`), FLV is append-only (survives crashes), HLS writes `hls-000001.ts…` + `hls.m3u8` aligned on key frames (`[hls] seg_duration`, `seg_num`).
- Recording resumes automatically after a publisher reconnects; `protocol.enable_mp4` / `enable_flv_record` enable always-on recording at startup (`*_yolo` streams are skipped).

## YOLO (optional)

Configured entirely through `.env`: `ZLM_YOLO_MODEL_DIR`, `ZLM_YOLO_MODEL`, `ZLM_YOLO_DEVICE` (`cpu`/`vulkan`/`metal`/`auto`), `ZLM_YOLO_CONFIDENCE`, input size, thread count, sample interval. Models are loaded lazily and cached (LRU); inference runs on a resident ffmpeg decoder frame, and results are kept per stream with stable IoU `trackId`s. `startYoloOverlay` spawns a decode → draw → encode pipeline that publishes `{stream}_yolo` back into the server.

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

Hook payloads mirror ZLM (`mediaServerId`, `app`, `stream`, `vhost`, `schema`, `id`, `ip`, `port`, `user_agent`, `params`); query parameters of the publish/play URL are forwarded in `params`, so token-based auth is decided externally. Idle streams are reclaimed after `general.streamNoneReaderDelayMS` according to `protocol.auto_close` / `on_stream_none_reader`.

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
  Core/ Codecs/ Configuration/ Protocols/{Flv,Rtmp,Rtsp}/ Http/ Recording/ Ai/ Api/ Server/
src/ZlmSharp.Cli/       executable entry point (product: zlmsharp.exe, NativeAOT)
samples/…Sample.Web/    embedding sample
tests/ZlmSharp.Tests/   xUnit unit + end-to-end tests
assets/                 app.ico (multi-size) + app-icon.png, generated by tools/make-icon.ps1
```

## Build, publish, test

```bash
dotnet build zlmsharp.sln                       # treat warnings as errors, AOT analyzers on
dotnet test  tests/ZlmSharp.Tests               # 238 tests
dotnet publish src/ZlmSharp.Cli -c Release -r win-x64   # NativeAOT single exe
```

NativeAOT needs MSVC + Windows SDK on Windows (clang + zlib1g-dev on Linux). Publishing drops the trimmed exe next to a generated `zlmsharp.ini`; the Win32 icon comes from `assets/app.ico`.

A real-model YOLO smoke test (not part of the normal suite):

```bash
ZLM_YOLO_TEST_MODEL=<path-to.onnx> dotnet test --filter YoloModelSmokeTests
```

## Known limitations

- Video is H.264 only (no H.265); audio is AAC.
- RTSP pull proxy uses TCP interleaved only; RTSP push side sends RR but never SR.
- HLS is a recording format today — `protocol.enable_hls` is parsed but not wired to playback.
- MP4 files are only playable after recording stops (moov is written at the end); use FLV for crash tolerance.
- Single process, in-memory state: no clustering, no RTSP/RTMP SSL, no WebRTC.

## Roadmap

Progress and detailed design notes live in `workplan.md` (kept out of git by `.gitignore`). Next phase:

1. **H.265 / HEVC end to end** — VPS/SPS/PPS parsing, RFC 7798 RTP, FLV `hvcC`, MP4 `hvc1`, TS `0x24`, RTSP/RTMP in/out and relay.
2. **Web admin page** — built-in static site (no frontend build step) over the existing `/index/api/*`: streams, sessions, recording, proxy/relay, AI results.
3. **Generic AI layer** — split frame source / `IStreamAnalyzer` / `IOverlayRenderer`, so YOLO becomes one plugin and OCR, license-plate or zone-intrusion analyzers can be added later.
4. **GB/T 28181 and JT/T 809** — SIP + PS-over-RTP camera onboarding, and vehicle registration/position/alarm ingestion.

## License

GPL-3.0 — see [LICENSE](LICENSE).
