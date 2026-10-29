using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ZlmSharp.Ai;

/// <summary>
/// 推理会话的宿主：首次使用某个模型时才加载（懒加载），之后按模型路径缓存复用。
/// 后端会话不支持并发推理（重入会抛异常），所以这里用信号量把调用排队。
/// </summary>
/// <remarks>
/// 实际推理引擎由 <see cref="IYoloBackendFactory"/> 注入；未注入时退化为
/// <see cref="UnavailableYoloBackendFactory"/>，<see cref="BackendAvailable"/> 为 false。
/// </remarks>
public sealed class YoloDetectorService : IDisposable
{
    private static readonly string[] ModelExtensions = [".onnx", ".pt", ".pth", ".sytrain"];

    private readonly YoloOptions _options;
    private readonly IYoloBackendFactory _backendFactory;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<string, IYoloInferenceBackend> _detectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _loadErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, YoloModelSummary> _summaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _usage = [];

    private string[]? _availableModels;
    private bool _disposed;

    public YoloDetectorService(
        YoloOptions options,
        IYoloBackendFactory? backendFactory = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _backendFactory = backendFactory ?? UnavailableYoloBackendFactory.Instance;
        _logger = logger;
    }

    public YoloOptions Options => _options;

    /// <summary>是否配置了默认模型（配置了模型目录但没指定默认模型时为 false，仍可用 <c>model=</c> 指定）。</summary>
    public bool Enabled => _options.ModelPath.Length > 0;

    /// <summary>当前构建是否接入了推理后端；false 时即便配置了模型也无法推理。</summary>
    public bool BackendAvailable => _backendFactory.IsAvailable;

    /// <summary>推理后端名（未接入时为 <c>unavailable</c>）。</summary>
    public string BackendName => _backendFactory.Name;

    /// <summary>默认模型绝对路径。</summary>
    public string ModelPath => _options.ModelPath;

    /// <summary>模型目录（可能为空）。</summary>
    public string ModelDir => _options.ModelDir;

    public string Device => _options.Device;

    /// <summary>是否已有加载好的推理会话。</summary>
    public bool Loaded
    {
        get
        {
            lock (_stateLock)
            {
                return _detectors.Count > 0;
            }
        }
    }

    /// <summary>已加载（缓存中）的模型文件名。</summary>
    public IReadOnlyList<string> LoadedModels
    {
        get
        {
            lock (_stateLock)
            {
                return _detectors.Keys.Select(Path.GetFileName).Select(name => name ?? string.Empty).ToArray();
            }
        }
    }

    /// <summary>模型目录下可用的模型文件名（目录未配置时为空）。</summary>
    public IReadOnlyList<string> AvailableModels
    {
        get
        {
            lock (_stateLock)
            {
                return _availableModels ??= ScanModels();
            }
        }
    }

    /// <summary>
    /// 把模型名解析成绝对路径；<paramref name="model"/> 为空时用默认模型。
    /// 只允许目录内的文件名（不接受路径与 <c>..</c>）。
    /// </summary>
    public string ResolveModelPath(string? model = null)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            if (_options.ModelPath.Length == 0)
            {
                throw new InvalidOperationException(
                    "YOLO 未启用：请在 .env 配置 ZLM_YOLO_MODEL（可配合 ZLM_YOLO_MODEL_DIR 只写文件名）");
            }

            return _options.ModelPath;
        }

        var name = model.Trim();
        if (name.IndexOfAny(['/', '\\']) >= 0 || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("模型名只能是文件名，不能包含路径", nameof(model));
        }

        if (!HasModelExtension(name))
        {
            throw new ArgumentException($"模型扩展名必须是 {string.Join("/", ModelExtensions)}", nameof(model));
        }

        if (_options.ModelDir.Length == 0)
        {
            throw new InvalidOperationException("未配置 ZLM_YOLO_MODEL_DIR，无法按模型名选择");
        }

        var path = Path.Combine(_options.ModelDir, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"模型不存在：{path}", path);
        }

        return path;
    }

    /// <summary>对 RGB24 图像做检测；<paramref name="confidence"/> 为空则用模型默认阈值。</summary>
    /// <exception cref="InvalidOperationException">未配置模型或模型加载失败。</exception>
    public async Task<YoloResult> DetectAsync(
        RgbImage image,
        float? confidence = null,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (confidence is { } threshold && threshold is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "置信度必须在 0~1 之间");
        }

        var modelPath = ResolveModelPath(model);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var detector = await GetOrLoadAsync(modelPath, cancellationToken).ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            var names = detector.ClassNames;
            var objects = new List<YoloObject>();

            try
            {
                foreach (var box in detector.Detect(image, confidence))
                {
                    objects.Add(ToObject(box, names));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"YOLO 推理失败（模型 {Path.GetFileName(modelPath)}，cls/obb 模型当前不受支持）：{exception.Message}",
                    exception);
            }

            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return new YoloResult(image.Width, image.Height, elapsed, objects);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>读取模型元信息（类别名等），不需要加载推理后端；模型未配置/读不到返回 null。</summary>
    public YoloModelSummary? ReadModelSummary(string? model = null)
    {
        string path;
        try
        {
            path = ResolveModelPath(model);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FileNotFoundException)
        {
            _logger?.LogDebug(exception, "无法解析 YOLO 模型，跳过元信息读取");
            return null;
        }

        lock (_stateLock)
        {
            if (_summaries.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        YoloModelSummary? summary;
        try
        {
            summary = _backendFactory.ReadModelInfo(path);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "读取 YOLO 模型元信息失败: {Model}", path);
            return null;
        }

        if (summary is null)
        {
            return null;
        }

        lock (_stateLock)
        {
            _summaries[path] = summary;
        }

        return summary;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var detector in _detectors.Values)
            {
                detector.Dispose();
            }

            _detectors.Clear();
            _usage.Clear();
        }

        _gate.Dispose();
    }

    /// <summary>调用方必须持有 <see cref="_gate"/>。</summary>
    private async Task<IYoloInferenceBackend> GetOrLoadAsync(string modelPath, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_detectors.TryGetValue(modelPath, out var cached))
            {
                TouchLocked(modelPath);
                return cached;
            }

            if (_loadErrors.TryGetValue(modelPath, out var failed))
            {
                throw new InvalidOperationException($"YOLO 模型加载失败：{failed.Message}", failed);
            }
        }

        var backendOptions = BuildBackendOptions();
        _logger?.LogInformation(
            "加载 YOLO 模型 {Model}（backend={Backend}, device={Device}, input={Width}x{Height}）",
            modelPath,
            _backendFactory.Name,
            backendOptions.Device,
            backendOptions.InputWidth,
            backendOptions.InputHeight);

        IYoloInferenceBackend detector;
        try
        {
            detector = await Task.Run(
                () => _backendFactory.Load(modelPath, backendOptions),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_stateLock)
            {
                _loadErrors[modelPath] = exception;
            }

            _logger?.LogError(exception, "YOLO 模型加载失败: {Model}", modelPath);
            throw new InvalidOperationException($"YOLO 模型加载失败：{exception.Message}", exception);
        }

        _logger?.LogInformation(
            "YOLO 模型加载完成 {Model}：类别数 {Count}，后端 {Backend}",
            Path.GetFileName(modelPath),
            detector.ClassNames.Count,
            detector.Name);

        lock (_stateLock)
        {
            _detectors[modelPath] = detector;
            TouchLocked(modelPath);
            EvictLocked();
        }

        return detector;
    }

    private void TouchLocked(string modelPath)
    {
        _usage.Remove(modelPath);
        _usage.Add(modelPath);
    }

    /// <summary>超出缓存上限时淘汰最久未用的模型，避免同时持有太多权重。</summary>
    private void EvictLocked()
    {
        var limit = Math.Max(1, _options.MaxCachedModels);
        while (_usage.Count > limit)
        {
            var oldest = _usage[0];
            _usage.RemoveAt(0);
            if (_detectors.Remove(oldest, out var detector))
            {
                _logger?.LogInformation("淘汰 YOLO 模型缓存 {Model}", Path.GetFileName(oldest));
                detector.Dispose();
            }
        }
    }

    private string[] ScanModels()
    {
        if (_options.ModelDir.Length == 0 || !Directory.Exists(_options.ModelDir))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(_options.ModelDir)
                .Where(HasModelExtension)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "扫描 YOLO 模型目录失败: {Dir}", _options.ModelDir);
            return [];
        }
    }

    private static bool HasModelExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return ModelExtensions.Any(item => string.Equals(item, extension, StringComparison.OrdinalIgnoreCase));
    }

    private YoloBackendOptions BuildBackendOptions() => new()
    {
        Device = _options.Device,
        InputWidth = _options.InputWidth,
        InputHeight = _options.InputHeight,
        Confidence = _options.Confidence,
        CpuThreads = _options.CpuThreads,
    };

    private static YoloObject ToObject(YoloRawDetection box, IReadOnlyDictionary<int, string> names) => new(
        box.ClassId,
        names.TryGetValue(box.ClassId, out var name) ? name : string.Empty,
        box.Confidence,
        box.X,
        box.Y,
        box.Width,
        box.Height);
}
