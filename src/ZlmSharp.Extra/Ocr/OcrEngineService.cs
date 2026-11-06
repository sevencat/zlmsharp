using Microsoft.Extensions.Logging;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;
using ZlmSharp.Ai;

namespace ZlmSharp.Extra.Ocr;

/// <summary>
/// OCR 引擎持有者：首次分析时懒加载 PP-OCRv6 模型（内置 tiny，或自定义 DET/REC/DICT），
/// 之后复用同一会话。
/// </summary>
/// <remarks>
/// PaddleOcrAll 的推理会话<b>不可重入</b>（内部有共享缓冲/线程池），并发调用会读到坏数据甚至让进程崩溃。
/// 分析调度器只对同一分析器串行，但 HTTP 单图分析（analyzeDetect）和叠加调度可能同时进来，
/// 所以这里再加一把全局锁，所有推理调用与释放都串行。
/// </remarks>
public sealed class OcrEngineService : IDisposable
{
    private readonly OcrOptions _options;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private PaddleOcrAll? _engine;
    private Exception? _error;
    private int _disposed;

    public OcrEngineService(OcrOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
    }

    public OcrOptions Options => _options;

    public bool Enabled => _options.Enabled;

    /// <summary>模型是否已加载完成。</summary>
    public bool Ready => Volatile.Read(ref _engine) is not null;

    /// <summary>最近一次加载/推理失败信息；无失败为 null。</summary>
    public string? LastError => Volatile.Read(ref _error)?.Message;

    /// <summary>配置启用且尚未出现加载错误（调度器据此决定是否采样）。</summary>
    public bool CanRun => Enabled && Volatile.Read(ref _error) is null;

    /// <summary>识别一张 RGB24 图像；首次调用会加载模型（可能耗时数百毫秒到数秒）。</summary>
    public async Task<PaddleOcrResult> RunAsync(RgbImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);

        var engine = await EnsureEngineAsync(cancellationToken).ConfigureAwait(false);

        // 会话不可重入：HTTP 单图分析与叠加调度可能并发，必须串行
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            // PaddleOcrAll.Run 接收交错像素内存：RgbImage 是 RGB24，可直接传
            return engine.Run(image.Pixels, image.Width, image.Height, image.Stride, ImagePixelFormat.Rgb24);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<PaddleOcrAll> EnsureEngineAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _engine) is { } ready)
        {
            return ready;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _engine) is { } loaded)
            {
                return loaded;
            }

            if (Volatile.Read(ref _error) is { } failed)
            {
                throw new InvalidOperationException($"OCR 引擎加载失败：{failed.Message}", failed);
            }

            try
            {
                var engine = await LoadAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _engine, engine);
                _logger?.LogInformation(
                    "OCR 引擎加载完成（{Model}，方向分类={Cls}，行线程={Workers}）",
                    _options.UseCustomModels ? "custom" : _options.Model,
                    _options.UseDirectionClassification,
                    _options.LineWorkerCount);
                return engine;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Volatile.Write(ref _error, exception);
                _logger?.LogError(exception, "OCR 引擎加载失败");
                throw new InvalidOperationException($"OCR 引擎加载失败：{exception.Message}", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PaddleOcrAll> LoadAsync(CancellationToken cancellationToken)
    {
        var options = new PaddleOcrOptions
        {
            UseDirectionClassification = _options.UseDirectionClassification,
            LineWorkerCount = Math.Max(1, _options.LineWorkerCount),
        };

        if (!_options.UseCustomModels)
        {
            return await PaddleOcrAll
                .LoadAsync(ChineseV6TinyModels.Default, options, cancellationToken)
                .ConfigureAwait(false);
        }

        var clsPath = _options.ClsModelPath.Length > 0 ? _options.ClsModelPath : null;
        return await PaddleOcrAll
            .LoadAsync(_options.DetModelPath, _options.RecModelPath, clsPath!, _options.DictPath, options, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 等在途推理结束再释放：Run 与 Dispose 竞争会让推理库读到已释放的缓冲
        var acquired = _runGate.Wait(TimeSpan.FromSeconds(5));
        try
        {
            _engine?.Dispose();
            _engine = null;
        }
        finally
        {
            if (acquired)
            {
                _runGate.Release();
            }

            _gate.Dispose();
            _runGate.Dispose();
        }
    }
}
