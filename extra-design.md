# ZlmSharp.Extra 设计：AI 实现剥离与第三方算法接入

本文描述把 AI（YOLO / OCR / 其它视觉算法）的具体实现从核心库 `ZlmSharp` 剥离到 `ZlmSharp.Extra` 的设计。
核心库只保留**契约（接口 + 数据模型）与顶层调用（HTTP API 入口）**；具体取帧、推理、跟踪、绘框、叠加管线全部下沉到 Extra，第三方算法以独立 DLL 的形式实现契约并接入。

## 实施状态（2026-11）

已按以下确认决策落地：

| 决策点 | 结论 |
|---|---|
| `YoloOptions` / `YoloResult` / `YoloSnapshot` 等类型 | 全部移出核心；`getYoloResult` 从通用 `AnalysisResult`（Box 图元 + `classId` Attribute）反向映射，`YoloResultStore` / `YoloSnapshot` 删除 |
| 通用端点命名 | `getAnalyzerList` / `analyzeDetect` / `startOverlay` / `stopOverlay` / `getOverlay`；旧 yolo 端点全部保留兼容 |
| 叠加流后缀 | 切换为 `_ai`（`ZLM_OVERLAY_SUFFIX` 可改回 `_yolo`）；前端通过 API 返回的 `overlayStream` 字段取名，无需改动 |
| 插件目录扫描 | **暂不实现**（`ZlmSharp.Extra.Plugins` 未创建）；第三方以编译期引用 + `AddAnalyzer` 接入（AOT 友好），设计保留在 §8.3 供后续实施 |
| ffmpeg/ffprobe 调用 | 已收敛到 `Media/IMediaToolkit` + `FfmpegToolkit`（抽帧 / 常驻解码「最新帧、顺序读」/ 常驻编码）；上层不再出现 `Process`，替换为进程内 libav 实现时只需换掉 `IMediaToolkit` 注册 |

已实现清单：核心契约（`Ai/AnalyzerCatalog.cs`、`Ai/OverlayModels.cs`、`AnalysisModels.cs` 扩展）、`src/ZlmSharp.Extra`（Capture / Yolo / Overlay / Analysis 四个目录 + `ExtraBuilder`/`AddZlmSharpExtra`）、`AnalysisScheduler` 替代 `StreamYoloMonitor`、`OverlayResultCache`、新端点与 DTO、`.env` 模板新键、CLI 接入 Extra、README 与测试更新。

---


## 1. 目标与非目标

### 目标

| 编号 | 目标 |
|---|---|
| G1 | 核心库不再包含 ffmpeg 子进程、图像解析、推理会话、跟踪、绘框等实现代码 |
| G2 | 核心库的 HTTP API 行为不变：未安装 Extra 时 `/index/api/yolo*`、`analyzeResult` 按“未启用”返回，装机后工作 |
| G3 | 新增算法只需实现 `IStreamAnalyzer`（算法级）或 `IYoloInferenceBackend`（推理内核级），**不需要碰核心库** |
| G4 | 支持三种结果消费方式：存快照供 API 查询、叠加成新流、**只跑分析不关心结果** |
| G5 | 算法实现可编译期注册（AOT 友好），也可 JIT 下扫描插件目录动态加载 |

### 非目标

- 不在本期实现“进程外算法服务”（gRPC / HTTP 推理）——只在扩展点预留位置。
- 不改变现有 API 的请求参数与返回 JSON 结构。
- 不承诺插件 DLL 与核心库跨大版本二进制兼容（见 §8.3）。

---

## 2. 现状盘点

当前 `src/ZlmSharp/Ai/` 下 18 个文件混在一起：既有可复用的契约，也有大量实现细节。

| 现有文件 | 性质 | 去向 |
|---|---|---|
| `Analysis/AnalysisModels.cs`（`AnalysisKind` / `AnalysisPrimitive` / `AnalysisResult` / `FrameSample` / `IStreamAnalyzer` / `IOverlayRenderer` / `AnalyzerRegistry` / `AnalysisResultStore`） | 契约 + 纯内存容器 | 拆分：契约留核心；`AnalyzerRegistry`、`AnalysisResultStore` 留核心（API 与测试直接使用） |
| `Analysis/YoloAnalyzer.cs` | YOLO 对通用分析层的适配 | → Extra |
| `Analysis/AnalysisRenderers.cs` | 把图元画到帧上 | → Extra |
| `RgbImage.cs` | 帧数据载体（分析器入参） | 留核心（无依赖，跨包契约必需） |
| `PpmImage.cs`、`FfmpegFrameGrabber.cs` | 图片解析 / 抽帧 | → Extra（核心 API 改为传原始字节） |
| `FfmpegDecoderPool.cs`、`FfmpegLiveDecoder.cs` | 常驻解码器 | → Extra |
| `YoloDetectorService.cs`、`YoloBackend.cs`、`YoloOptions.cs`、`YoloResult*.cs` | 推理宿主、后端适配、模型缓存 | → Extra（`IYoloBackendFactory` 也随之进 Extra） |
| `ObjectTracker.cs`、`FrameOverlay.cs` | 跟踪、绘制 | → Extra |
| `YoloOverlayManager.cs` | 解码→绘制→编码管线 | → Extra，对外只留 `IOverlayController` |
| `StreamYoloMonitor.cs` | 采样调度后台服务 | → Extra，提升为通用 `AnalysisScheduler` |
| `YoloServiceCollectionExtensions.cs` | `AddZlmSharpYolo` | 由 `AddZlmSharpExtra` 取代 |
| `Api/ApiEndpoints.cs` 中的 yolo 段 | 顶层调用 | 留核心，改为调用可选宿主接口 |
| `Server/ZlmSharpHostingExtensions.cs` 的 `services.AddZlmSharpYolo(...)` | 顶层装配 | 从 `AddZlmSharp` 移除，改由宿主显式注册 Extra |

> 结论：核心库最终只剩 `ZlmSharp.Ai`（契约）+ `Api/ApiEndpoints.cs`（入口）。AI 实现零残留。

---

## 3. 总体架构

```
┌──────────────────────────────────────────────────────────────────┐
│ ZlmSharp（核心库，无 AI 实现）                                    │
│                                                                  │
│  ZlmSharp.Ai（契约）          ZlmSharp.Ai.Analysis（契约）        │
│   RgbImage                     AnalysisKind / Primitive / Result  │
│   AnalyzerDescriptor           IStreamAnalyzer / IOverlayRenderer │
│   AnalyzerRegistry             FrameSample                        │
│   AnalysisResultStore                                            │
│   IAnalyzerCatalog / IImageAnalysisService / IOverlayController    │
│                                                                  │
│  Api/ApiEndpoints.cs  ── GetService<I…>() ──┐  未注册时降级为“未启用”│
│  Server/ZlmSharpHostingExtensions.cs        │                    │
└─────────────────────────────────────────────┼────────────────────┘
                                              │ 引用（单向）
┌─────────────────────────────────────────────▼────────────────────┐
│ ZlmSharp.Extra（官方实现 + 插件宿主，可裁剪）                      │
│                                                                  │
│  Capture/   FfmpegFrameSource（常驻解码池）、图片解码器             │
│  Analysis/  AnalysisScheduler（BackgroundService）、AnalyzerHost   │
│  Yolo/      YoloAnalyzer、YoloDetectorService、ObjectTracker       │
│  Overlay/   FfmpegOverlayPipeline、FrameOverlay、OverlayController │
│  Hosting/   ExtraBuilder、ServiceCollectionExtensions             │
└─────────────────────────────────────────────▲────────────────────┘
                                              │ 引用（单向）
┌─────────────────────────────────────────────┴────────────────────┐
│ 第三方算法 DLL（Acme.Ocr.ZlmSharp.dll …）                          │
│  L1：IStreamAnalyzer + IOverlayRenderer（推荐，不接触 ffmpeg）      │
│  L2：IYoloInferenceBackend / IYoloBackendFactory（只换推理内核）    │
│  L3：IExtraPlugin（打包自己的注册逻辑，供目录扫描）                 │
└──────────────────────────────────────────────────────────────────┘
```

依赖方向严格单向：`第三方 → (Extra) → 核心`。核心库**不得** `ProjectReference` Extra。

---

## 4. 核心库保留的契约

### 4.1 数据模型（`ZlmSharp.Ai.Analysis`）

```csharp
public enum AnalysisKind { Box, Polygon, Text, Mask }

public readonly record struct AnalysisPoint(double X, double Y);

public sealed record AnalysisPrimitive(
    AnalysisKind Kind,
    string Label,                       // 类别名 / 文本
    double Score,
    int? TrackId = null,
    double? X = null, double? Y = null, double? Width = null, double? Height = null,
    IReadOnlyList<AnalysisPoint>? Points = null,
    string? Text = null,
    IReadOnlyDictionary<string, string>? Attributes = null);   // classId 等分析器私有字段

public sealed record AnalysisResult(
    string AnalyzerId,
    StreamKey Key,
    DateTimeOffset At,
    long ElapsedMs,
    int Width, int Height,
    IReadOnlyList<AnalysisPrimitive> Primitives,
    string? Error = null);

/// <summary>送进分析器的一帧 RGB24。</summary>
public sealed record FrameSample(StreamKey Key, RgbImage Image, long Dts = 0);
```

保留现有 `RgbImage(byte[] Pixels, int Width, int Height, int Stride)`：它是算法入参，若放 Extra，第三方就必须引用 Extra 才能实现分析器，违背 G3。

### 4.2 算法契约

```csharp
public interface IStreamAnalyzer
{
    string Id { get; }               // 出现在 API / 结果存储里，如 yolo、ocr、plate
    string DisplayName { get; }
    bool Enabled { get; }
    Task<AnalysisResult> AnalyzeAsync(FrameSample frame, CancellationToken cancellationToken);
}

public interface IOverlayRenderer
{
    string Id { get; }                                    // 通常与分析器 Id 相同
    bool CanRender(AnalysisResult result);
    void Render(RgbImage image, AnalysisResult result);    // 直接改 Pixels
}

/// <summary>可选：结果旁路（告警、入库、转发）。只跑不存模式下用。</summary>
public interface IAnalysisSink
{
    ValueTask PublishAsync(AnalysisResult result, CancellationToken cancellationToken);
}
```

约束（写进 XML 文档与 README）：

- 分析器必须是**单例、线程安全**；同一分析器的调用由调度器串行化（推理会话通常不可重入）。
- `AnalyzeAsync` 内部不得吞掉 `OperationCanceledException`；其它异常可自行转成 `AnalysisResult.Error`，调度器也会兜底。
- 渲染器必须只依赖传入的 `AnalysisResult`，不得自己再去解码。

### 4.3 结果消费方式与注册项

```csharp
[Flags]
public enum AnalysisOutputs
{
    None     = 0,   // 只跑分析：不存快照、不叠加（第三方在 AnalyzeAsync 内自行处理结果）
    Snapshot = 1,   // 写入 AnalysisResultStore，供 analyzeResult/getYoloResult 查询
    Overlay  = 2,   // 写入叠加用的最新结果缓存
    Sink     = 4,   // 交给 IAnalysisSink
}

public sealed record AnalyzerRegistration(IStreamAnalyzer Analyzer)
{
    public AnalysisOutputs Outputs { get; init; } = AnalysisOutputs.Snapshot;
    public int SampleIntervalMs { get; init; }   // 0 = 用全局 ZLM_ANALYSIS_SAMPLE_INTERVAL_MS
    public int MaxResultAgeMs { get; init; } = 5000;
}
```

`AnalyzerRegistry`（核心，线程安全容器）负责持有 `AnalyzerRegistration`，提供 `All / Enabled / Get(id)`；
`AnalysisResultStore`（核心）继续按 `(流, 分析器)` 保存最近一次结果。

### 4.4 供 API 调用的宿主接口

核心 API 用 `GetService<T>()` 取，取不到即视为“AI 未接入”，返回现有错误文案。四个小接口，避免上帝接口：

```csharp
/// <summary>yoloInfo / getAnalyzerList：分析器与模型清单。</summary>
public interface IAnalyzerCatalog
{
    IReadOnlyList<AnalyzerDescriptor> List();
    AnalyzerDescriptor? Get(string analyzerId);
}

public sealed record AnalyzerDescriptor(
    string Id,
    string DisplayName,
    bool Enabled,
    bool Ready,                                       // 模型已加载 / 后端可用
    string? Model,
    string? ModelDir,
    string? Device,
    string? Backend,
    IReadOnlyList<string> Labels,                     // 类别名（OCR 可为空）
    IReadOnlyList<string> AvailableModels,
    IReadOnlyList<string> LoadedModels,
    IReadOnlyDictionary<string, string> Settings,      // input_width / confidence / …（字符串化，便于通用展示）
    string? Error = null);

/// <summary>yoloDetect / analyzeDetect：单图或从流地址抓一帧后分析。</summary>
public sealed record ImageAnalysisRequest(string AnalyzerId)
{
    public ReadOnlyMemory<byte>? Image { get; init; }  // 原始编码字节，格式由实现声明
    public string? SourceUrl { get; init; }
    public string? Model { get; init; }
    public float? Confidence { get; init; }
}

public interface IImageAnalysisService
{
    IReadOnlyList<string> ImageFormats { get; }        // 如 ["ppm", "jpeg", "png"]
    Task<AnalysisResult> AnalyzeAsync(ImageAnalysisRequest request, CancellationToken cancellationToken);
}

/// <summary>startYoloOverlay / stopYoloOverlay / getYoloOverlay。</summary>
public enum OverlayState { Starting, Running, Stopped, Failed }

public sealed record OverlayRequest(string Vhost, string App, string Stream)
{
    public string? AnalyzerId { get; init; }
    public int? Fps { get; init; }
    public int BitrateKbps { get; init; }
    public int LabelScale { get; init; } = 2;
}

public sealed record OverlayStatus(
    string Vhost, string App, string Stream,
    string OverlayStream, string InputUrl, string OutputUrl,
    OverlayState State, int Width, int Height, int Fps, int BitrateKbps,
    long Frames, DateTimeOffset StartedAt, double ElapsedMs, string? Error);

public interface IOverlayController
{
    bool IsAvailable { get; }
    string OverlayStreamName(string stream);              // 默认 "{stream}_yolo"，可配置
    bool IsOverlayStream(string? stream);                 // 调度器跳过派生流，避免回路
    OverlayStatus Start(OverlayRequest request);
    bool Stop(string vhost, string app, string stream);
    OverlayStatus? Get(string vhost, string app, string stream);
    IReadOnlyList<OverlayStatus> List(string vhost = "", string app = "", string stream = "");
}
```

### 4.5 配置契约

`EnvOptions` 保留 `ZLM_YOLO_*` 全部键（兼容旧 `.env`），新增通用键（见 §10）。
`YoloOptions` 随实现移入 Extra，`EnvOptions.ResolveYoloModelPath/Dir` 保留（纯路径解析，无 AI 依赖）。

---

## 5. ZlmSharp.Extra 结构

```
src/ZlmSharp.Extra/
  ZlmSharp.Extra.csproj                  # net8.0，引用 ZlmSharp，无第三方包
  ExtraServiceCollectionExtensions.cs    # AddZlmSharpExtra(...)
  Hosting/
    IExtraBuilder.cs / ExtraBuilder.cs   # 注册 DSL（分析器 / 渲染器 / sink / 后端）
    ExtraPlugin.cs                       # IExtraPlugin 契约（供插件 DLL 实现）
    PluginLoader.cs                      # 目录扫描 + AssemblyLoadContext（JIT）
  Media/
    IMediaToolkit.cs                     # ffmpeg/ffprobe 抽象：GrabFrameAsync / OpenSampler / OpenReader / OpenSink
    FfmpegToolkit.cs                     # 进程实现（唯一的 Process 使用点）；将来可换 libav DLL 实现
  Capture/
    FfmpegDecoderPool.cs                 # 基于 IMediaToolkit.OpenSampler 的常驻解码池
    PpmImage.cs                          # PPM 解析（抽帧产物的解码）
  Analysis/
    AnalysisScheduler.cs                 # BackgroundService：采样 → 分析 → 分发结果
    AnalysisHost.cs                      # IAnalyzerCatalog / IImageAnalysisService 实现
    LatestResultCache.cs                 # 叠加用的每流最新结果（按分析器）
  Yolo/
    YoloAnalyzer.cs / YoloDetectorService.cs / YoloBackend.cs
    ObjectTracker.cs / YoloOptions.cs / YoloResult.cs / YoloResultStore.cs
  Overlay/
    OverlayController.cs                 # IOverlayController 实现
    FfmpegOverlayPipeline.cs             # 原 YoloOverlayManager + YoloOverlaySession
    FrameOverlay.cs / AnalysisRenderers.cs
```

### 5.1 调度器（唯一的“执行者”）

```csharp
internal sealed class AnalysisScheduler : BackgroundService
{
    // 500ms tick；每路流按各自 SampleIntervalMs 限速；每分析器一把信号量（不可重入）
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var tick in ...)
        foreach (var source in _registry.Sources.ToArray())
        {
            if (_overlays.IsOverlayStream(source.Key.Stream)) continue;   // 不检测派生流
            foreach (var reg in _analyzers.Enabled)
            {
                if (!ShouldSample(source.Key, reg)) continue;
                var frame = await _frames.TryGrabAsync(source.Key, w, h, GrabTimeout, ct);
                if (frame is null) continue;

                var result = await reg.Analyzer.AnalyzeAsync(new FrameSample(source.Key, frame), ct);

                if (reg.Outputs.HasFlag(AnalysisOutputs.Overlay)) _latest.Set(result);
                if (reg.Outputs.HasFlag(AnalysisOutputs.Snapshot)) _store.Set(result);
                foreach (var sink in _sinks) await sink.PublishAsync(result, ct);
                if (reg.Outputs == AnalysisOutputs.None) _pipelineCounters.Touch(reg.Analyzer.Id); // 仅计数/日志
            }
        }
    }
}
```

要点：

- **一路流一个解码器**，多个分析器共享同一帧（先用 `IFrameSource` 缓存最新帧，避免 N 个 ffmpeg）。
- 分析器串行执行；不满足间隔的流直接跳过，永远只处理“最新帧”，不做积压。
- 分析器抛异常：记日志 + 生成 `AnalysisResult.Error`，不影响其它分析器与其它流。
- 流关闭时清理 `LatestResultCache` / `AnalysisResultStore`（复用现有逻辑）。

### 5.2 叠加管线

`FfmpegOverlayPipeline` 保持现有实现思路：ffmpeg 解码 HTTP-FLV → RGB24 → 渲染器链 → ffmpeg 编码推回 RTMP。

变化点：

- 结果来源从 `YoloResultStore` 改为 `LatestResultCache`（按 `AnalyzerId` 取）。
- 绘制改为遍历已注册 `IOverlayRenderer`，只调用 `CanRender(result)` 为真的渲染器。
- 输出流名由 `IOverlayController.OverlayStreamName` 决定（默认 `_yolo` 兼容旧行为，可配 `_ai`）。
- 第三方渲染器注册进来就生效，无需改管线代码。

---

## 6. 使用方（宿主）如何装配

```csharp
// CLI / 嵌入式宿主
builder.Services.AddZlmSharp(...);                       // 核心：契约 + 协议 + API，零 AI 依赖

builder.Services.AddZlmSharpExtra(extra =>
{
    extra.AddAnalyzer<YoloAnalyzer>(o => o.Outputs = AnalysisOutputs.Snapshot | AnalysisOutputs.Overlay);
    extra.AddAnalyzer<AcmeOcrAnalyzer>(o =>
    {
        o.Outputs = AnalysisOutputs.None;                 // 只分析，不管结果
        o.SampleIntervalMs = 2000;
    });
    extra.AddRenderer<YoloBoxRenderer>();
    extra.AddRenderer<OcrTextRenderer>();
    extra.AddYoloBackendFactory<SimdYoloBackendFactory>();  // L2
    extra.LoadPlugins("./plugins");                        // L3，仅 JIT 构建
});
```

`AddZlmSharpExtra` 内部完成：注册 `AnalysisScheduler`（HostedService）、`FfmpegFrameSource`、`OverlayController`、`AnalysisHost`（三个宿主接口）、`AnalyzerRegistry`、`AnalysisResultStore` 的单例绑定。

关闭方式：不调用 `AddZlmSharpExtra` 即可；或 `.env` 里 `ZLM_EXTRA_ENABLE=0`（注册了也整体不启用，API 按未启用返回）。

---

## 7. 三种使用形态

| 形态 | 注册配置 | 行为 | 典型场景 |
|---|---|---|---|
| 快照分析 | `Outputs = Snapshot` | 结果进 `AnalysisResultStore`，`analyzeResult` 可查 | 页面显示检测框、轮询最新结果 |
| 叠加成流 | `Snapshot \| Overlay` + 渲染器 | 结果进快照，同时绘制成 `{stream}_yolo` 新流 | 客户看带框画面、录像留证 |
| 只跑分析 | `Outputs = None`（或 `Sink`） | 不存、不画；结果由分析器内部消费或经 `IAnalysisSink` 旁路 | OCR 事件上报、区域入侵告警、只管计数 |

“只跑分析”形态下 API 不会返回该分析器的数据（`analyzeResult` 查不到），这是刻意的：避免无用数据占内存，也避免第三方算法结果被误读。若需要旁路结果，注册 `IAnalysisSink`。

单图分析（不采样）走 `IImageAnalysisService`：`analyzeDetect?analyzer=ocr`，与调度器完全解耦。

---

## 8. 第三方接入方式

### 8.1 L1：实现分析器（推荐）

```csharp
// Acme.Ocr.ZlmSharp.dll —— 只引用 ZlmSharp
using ZlmSharp.Ai.Analysis;

public sealed class AcmeOcrAnalyzer : IStreamAnalyzer
{
    public string Id => "acme-ocr";
    public string DisplayName => "Acme 车牌/文字识别";
    public bool Enabled => true;

    public async Task<AnalysisResult> AnalyzeAsync(FrameSample frame, CancellationToken ct)
    {
        var boxes = await AcmeNative.OcrAsync(frame.Image.Pixels, frame.Image.Width, frame.Image.Height, ct);
        var primitives = boxes.Select(b => new AnalysisPrimitive(
            AnalysisKind.Text, b.Label, b.Score,
            X: b.X, Y: b.Y, Width: b.W, Height: b.H, Text: b.Text)).ToArray();

        return new AnalysisResult(Id, frame.Key, DateTimeOffset.Now, b.ElapsedMs,
                                  frame.Image.Width, frame.Image.Height, primitives);
    }
}
```

第三方**不需要**：ffmpeg、解码器、采样定时器、HTTP 层。

### 8.2 L2：只替换 YOLO 推理内核

已有 `IYoloInferenceBackend` / `IYoloBackendFactory`（随 `YoloDetectorService` 一起进 Extra）：

```csharp
public interface IYoloInferenceBackend : IDisposable
{
    string Name { get; }
    IReadOnlyDictionary<int, string> ClassNames { get; }
    IReadOnlyList<YoloRawDetection> Detect(RgbImage image, float? confidence);
}

public interface IYoloBackendFactory
{
    string Name { get; }
    bool IsAvailable { get; }
    IYoloInferenceBackend Load(string modelPath, YoloBackendOptions options);
    YoloModelSummary? ReadModelInfo(string modelPath);
}
```

适配 ONNX Runtime / TensorRT / 厂商 SDK 时只实现这两个接口，前后处理、跟踪、模型缓存、API 全部复用。未注册时退化为 `UnavailableYoloBackendFactory`（现有行为）。

### 8.3 L3：插件包（可选）

```csharp
public interface IExtraPlugin
{
    string Name { get; }
    int ContractVersion { get; }        // 与 ExtraServiceCollectionExtensions.ContractVersion 比对
    void Configure(IExtraBuilder builder);
}
```

约定：

- 插件 DLL 与其私有依赖放在 `./plugins/<name>/` 下，用 `AssemblyLoadContext` + `AssemblyDependencyResolver` 隔离加载，避免依赖冲突。
- 扫描 `*.dll` 中公共非抽象 `IExtraPlugin` 实现；加载或 `Configure` 抛异常只记日志，不让宿主启动失败。
- 单个插件注册的东西不可卸载（.NET 限制），设计上按“启动期一次性加载”对待。

**版本策略**：契约（`ZlmSharp.Ai` 命名空间下的公共类型）视为稳定 ABI。

- 只增不改：新版本只能给接口加**默认实现成员**或新增接口；
- 宿主与插件都在 `ContractVersion` 上比对，不匹配时拒绝加载并给出明确日志；
- 若将来需要真正独立的 ABI，再把契约拆成 `ZlmSharp.Ai.Abstractions` 小包（本期不做，避免项目数膨胀）。

### 8.4 AOT 约束（重要）

CLI 目前是 `PublishAot=true`，**NativeAOT 下无法 `Assembly.LoadFrom`**（`PlatformNotSupportedException`）。因此：

| 部署形态 | 第三方接入方式 |
|---|---|
| NativeAOT 单文件 exe | 编译期集成：第三方提供**原生库**（`.dll`/`.so`，cdecl 导出）或 ONNX Runtime 原生库，托管适配器随宿主编译（`ProjectReference` + `TrimmerRootAssembly` + 显式 `AddAnalyzer<T>()`）；插件目录扫描不可用 |
| JIT 部署（`dotnet run` / `-p:PublishAot=false`） | 支持 §8.3 目录扫描 |
| 进程外算法服务（预留） | 第三方 dll 跑在自己进程，宿主侧只实现一个 HTTP/gRPC 客户端分析器（`IStreamAnalyzer`），天然规避 AOT 与 ABI 问题 |

为避免 AOT 分析器把 `Assembly.LoadFrom` 判为错误（`TreatWarningsAsErrors`），插件加载器单独放 `ZlmSharp.Extra.Plugins`（`IsAotCompatible=false`），核心与 `ZlmSharp.Extra` 保持 AOT 干净；CLI 默认**不**引用该包。

---

## 9. HTTP API 影响（保持兼容）

| 端点 | 现在的数据来源 | 迁移后 |
|---|---|---|
| `getApiList` | 常量数组 | 不变（含新端点） |
| `yoloInfo` | `YoloDetectorService` 属性 | `IAnalyzerCatalog.Get("yolo")` → 映射成 `ApiYoloInfoData`（字段不变，`classNames` 来自 `Labels`） |
| `yoloDetect` | `YoloDetectorService` + `FfmpegFrameGrabber` + `PpmImage` | `IImageAnalysisService.AnalyzeAsync(analyzerId: "yolo", Image: body)`；PPM 解析在 Extra |
| `getYoloResult` | `YoloResultStore` | `AnalysisResultStore.List(..., "yolo")` → 映射成 `ApiYoloSnapshot`（Box 图元 ↔ `ApiYoloObject`，`classId` 取 `Attributes`） |
| `analyzeResult` | `AnalysisResultStore` | 不变 |
| `startYoloOverlay` / `stopYoloOverlay` / `getYoloOverlay` | `YoloOverlayManager` | `IOverlayController`，`AnalyzerId` 默认 `yolo` |

新增（不改旧端点）：

| 端点 | 说明 |
|---|---|
| `getAnalyzerList` | 返回 `AnalyzerDescriptor[]`，UI 可发现第三方分析器 |
| `analyzeDetect?analyzer=&model=&confidence=` | 通用单图分析（body 或 `url=`），返回 `AnalysisResult` |
| `startOverlay` / `stopOverlay` / `getOverlay` | 通用叠加，`analyzer=` 指定用哪个结果源 |

未注册 Extra 时：所有上述端点的 `GetService<T>()` 返回 null，走现有“未启用/未接入”分支，**错误码与文案保持现状**。

---

## 10. 配置项

| 键 | 默认 | 说明 |
|---|---|---|
| `ZLM_EXTRA_ENABLE` | `1` | 新增。`0` 时即使注册了 Extra 也不启动调度与叠加 |
| `ZLM_EXTRA_PLUGINS` | `./plugins` | 新增。插件目录（仅 JIT 生效） |
| `ZLM_ANALYSIS_SAMPLE_INTERVAL_MS` | 取 `ZLM_YOLO_SAMPLE_INTERVAL_MS`（1000） | 新增，全局采样间隔 |
| `ZLM_ANALYSIS_ENABLED` | 空 = 全部已注册分析器 | 新增，逗号分隔白名单（如 `yolo,acme-ocr`） |
| `ZLM_ANALYSIS_MAX_RESULT_AGE_MS` | `5000` | 新增，叠加使用的结果最大年龄 |
| `ZLM_OVERLAY_SUFFIX` | `_yolo` | 新增，叠加流后缀；改为 `_ai` 时旧客户端需同步（默认不改） |
| `ZLM_YOLO_MODEL_DIR` / `ZLM_YOLO_MODEL` / `ZLM_YOLO_DEVICE` / `ZLM_YOLO_CONFIDENCE` / `ZLM_YOLO_INPUT_*` / `ZLM_YOLO_CPU_THREADS` / `ZLM_YOLO_SAMPLE_INTERVAL_MS` | 原值 | 全部保留，由 Extra 读取 |

`.env` 模板（`DotEnv.cs`）补一段通用分析配置示例，`ZLM_YOLO_*` 继续保留并标注“仅 YOLO 分析器使用”。

---

## 11. 工程与打包

```
src/ZlmSharp/ZlmSharp.csproj                 # 核心；不引用 Extra；不引用推理引擎
src/ZlmSharp.Extra/ZlmSharp.Extra.csproj     # 引用 ZlmSharp；AOT 兼容（IsAotCompatible=true）
src/ZlmSharp.Extra.Plugins/…                 # 可选；IsAotCompatible=false；仅 JIT 宿主引用
src/ZlmSharp.Cli/ZlmSharp.Cli.csproj         # 引用 ZlmSharp + ZlmSharp.Extra（不引用 Plugins）
tests/ZlmSharp.Tests/…                       # 引用 ZlmSharp；新增 ZlmSharp.Extra 引用
samples/ZlmSharp.Sample.Plugin/…             # 新增：最小第三方分析器示例（可选）
```

其他要点：

- `AddZlmSharp` 去掉 `services.AddZlmSharpYolo(...)`；如宿主未注册 Extra，核心行为与“YOLO 未配置”完全一致。
- `ApiJsonContext` 需为 `getAnalyzerList` / `analyzeDetect` / `startOverlay` 的 DTO 补 `JsonSerializable`，避免 AOT 下反射序列化失败。
- `ZlmSharp.Extra` 内所有类型不加 `InternalsVisibleTo` 之外的隐藏反射用法；插件加载代码集中在 Plugins 包。
- 发布体积：不注册 Extra 时不会被 CLI 引用，AOT 裁剪后核心 exe 不再包含 ffmpeg 管线与 YOLO 代码。

---

## 12. 迁移步骤

### P0 契约先行（不改变行为）

1. 在核心 `Ai/` 下新增 §4 的契约文件（`IAnalyzerCatalog` / `IImageAnalysisService` / `IOverlayController` / `AnalyzerDescriptor` / `AnalysisOutputs` / `AnalyzerRegistration` / `IAnalysisSink`）。
2. `ApiEndpoints` 改为“优先取新接口，取不到回退旧实现”（临时 `#if`/分支），保证旧测试全绿。
3. 新增配置键解析与默认值，写单测。

验收：`dotnet build` 0 warning；`dotnet test` 全绿；端点行为无变化。

### P1 搬迁实现

1. 新建 `src/ZlmSharp.Extra`，把 §2 表中“→ Extra”文件整体 `git mv` 过去，命名空间改为 `ZlmSharp.Extra.*`。
2. 实现 `AnalysisScheduler`（替代 `StreamYoloMonitor`）、`FfmpegFrameSource`、`OverlayController`、`AnalysisHost`。
3. `AddZlmSharpExtra` 落地；CLI 与 Sample 显式注册；`AddZlmSharp` 移除 YOLO 注册。
4. `ApiEndpoints` 删除回退分支，只依赖核心契约。
5. 测试项目补 `ProjectReference` 到 Extra，搬迁 `YoloTests` / `YoloOverlayTests` / `FfmpegLiveDecoderTests` / `ObjectTrackerTests` / `YoloModelSmokeTests` 的 `using`。

验收：旧 API 返回体（字段名/顺序/错误文案）与迁移前 diff 为空；YOLO 冒烟测试可跑；`dotnet publish -r win-x64` 成功。

### P2 第三方能力

1. `ZlmSharp.Extra.Plugins`：目录扫描、`IExtraPlugin`、`ContractVersion` 校验、失败隔离。
2. 通用端点 `getAnalyzerList` / `analyzeDetect` / `startOverlay…` + WebUI 适配（`AiView.vue` 增加分析器下拉）。
3. `samples/ZlmSharp.Sample.Plugin`：一个不用任何推理引擎的假分析器（例如“每 5 秒返回一个随机框”），验证 L1 全链路与 `Outputs = None` 模式。
4. README 增加“第三方算法接入”章节与 AOT 限制说明。

### P3 清理

- 删除核心库中残留的 `Yolo*` 类型与 `using ZlmSharp.Ai;`（API DTO 除外，DTO 保留兼容）。
- `getYoloResult` 若确认无客户端依赖，标注为 deprecated；`getAnalyzerList` 作为正式替代。

---

## 13. 风险与取舍

| 风险 | 影响 | 对策 |
|---|---|---|
| 契约放在核心库，插件与核心版本耦合 | 升级核心可能破坏旧插件 | 只增不改 + `ContractVersion` 校验；必要时再拆 `ZlmSharp.Ai.Abstractions` |
| NativeAOT 不支持动态加载 | 第三方无法“丢 dll 进目录”就生效 | 明确两条路线：AOT 编译期集成（原生库）/ JIT 目录扫描；文档写清 |
| 每分析器都要一帧，容易重复解码 | CPU 翻倍 | `IFrameSource` 共享最新帧 + 每流单解码器；多分析器复用同一帧 |
| “只跑不存”缺乏可观测性 | 出问题难排查 | 每分析器计数（执行次数/耗时/错误）+ Debug 日志；可加 `getAnalyzerStats`（P2 可选） |
| 叠加回路（对派生流再分析） | 无限套娃、CPU 打满 | 调度器统一 `IOverlayController.IsOverlayStream` 过滤；该判断来自核心接口，第三方无需知道后缀 |
| 第三方分析器阻塞/泄漏 | 拖慢整条调度 | 每分析器独立信号量 + 超时取消 + 异常兜底；`Dispose` 由 DI 容器负责 |
| 结果内存增长 | 多分析器 × 多流 | `AnalysisResultStore` 只存“每流每分析器最近一次”；`Outputs=None` 完全不存 |

---

## 14. 一页结论

- **核心库** = `ZlmSharp.Ai`（约 6 个契约文件 + 2 个内存容器）+ `ApiEndpoints` 的可选服务调用；不含任何 ffmpeg / 推理 / 绘图代码。
- **ZlmSharp.Extra** = 官方参考实现（YOLO）+ 通用调度（采样/分发）+ 叠加管线 + 注册 DSL；默认不启用，宿主显式 `AddZlmSharpExtra`。
- **第三方 DLL** = 实现 `IStreamAnalyzer`（算法）或 `IYoloInferenceBackend`（内核），可选实现 `IOverlayRenderer` / `IAnalysisSink` / `IExtraPlugin`；通过 `IExtraBuilder` 或插件目录接入。
- **三种形态** = `Snapshot`（可查询）、`Snapshot|Overlay`（可观看）、`None`（只跑不管结果）。
- **兼容性** = 旧端点、旧 `.env`、旧 JSON 结构全部不变；未安装 Extra 时行为等同“AI 未启用”。
