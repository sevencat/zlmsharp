# zlmsharp

<img src="assets/app-icon.png" width="96" align="right" alt="zlmsharp" />

A C#/.NET 8 port of the [ZLMediaKit](https://github.com/ZLMediaKit/ZLMediaKit) streaming core: ingest, media source fan-out, protocol conversion and a ZLM-compatible HTTP API — as a single small binary plus one class library.

**English** | [简体中文](README.cn.md)

- RTMP publish / play (HEVC via the Enhanced RTMP `hvc1` ex-header), RTSP publish / play (TCP interleaved + UDP), HTTP-FLV, WebSocket-FLV; video is H.264 / H.265
- One `MediaSource` per stream: ring buffer, GOP snapshot, frame-level fan-out with per-reader drop accounting
- MP4 / FLV / HLS(TS) recording, RTMP relay push, RTSP/RTMP pull proxy
- Hooks (`on_publish` / `on_play` / `on_stream_none_reader`), session registry, idle-stream reclamation
- Optional YOLO detection / overlay on top of any live stream (pure managed inference, no native deps)
- NativeAOT publish friendly; no reflection-heavy configuration binding

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
