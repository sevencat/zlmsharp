using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ZlmSharp.Ai;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Extra.Analysis;

namespace ZlmSharp.Extra.Overlay;

/// <summary>
/// 结果叠加流：ffmpeg 解码本地 HTTP-FLV → 在 RGB24 帧上用注册的 <see cref="IOverlayRenderer"/> 绘制
/// → ffmpeg 编码推回本机 RTMP，输出 <c>{stream}_ai</c> 可直接播放。
/// </summary>
/// <remarks>
/// 本项目没有内置 H.264 解码/编码器，所以解码与编码都交给 ffmpeg 子进程，中间只传原始 RGB24 帧。
/// 绘制内容来自 <see cref="OverlayResultCache"/> 中各分析器最近一次结果（采样间隔秒级刷新），
/// 不对每一帧推理；叠加流本身（<c>*_ai</c>）不会被定时检测采样，避免回路。
/// </remarks>
public sealed class YoloOverlayManager : IOverlayController, IDisposable
{
    private static readonly TimeSpan DefaultResultMaxAge = TimeSpan.FromSeconds(5);

    private readonly MediaSourceRegistry _registry;
    private readonly OverlayResultCache _results;
    private readonly OverlayComposer _composer;
    private readonly AppOptions _options;
    private readonly string _suffix;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<StreamKey, OverlaySession> _sessions = new();
    private readonly object _gate = new();
    private int _disposed;

    public YoloOverlayManager(
        MediaSourceRegistry registry,
        AppOptions options,
        OverlayResultCache results,
        OverlayComposer composer,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(composer);

        _registry = registry;
        _options = options;
        _results = results;
        _composer = composer;
        _suffix = options.Env.OverlaySuffix;
        _logger = logger;
    }

    public string OverlayStreamName(string stream) => stream + _suffix;

    /// <summary>是否是叠加流（定时检测应跳过，避免对叠加结果再检测形成回路）。</summary>
    public bool IsOverlayStream(string? stream) =>
        stream is not null && stream.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 开始（或重启）一路叠加流。源流必须在线且已收到关键帧（需要分辨率）。
    /// </summary>
    /// <exception cref="ArgumentException">参数非法（含对叠加流再叠加）。</exception>
    /// <exception cref="InvalidOperationException">源流不在线/分辨率未知/端口未开启。</exception>
    public OverlayStatus Start(
        string vhost,
        string app,
        string stream,
        int? fps = null,
        int bitrateKbps = 0,
        int labelScale = 2,
        string? analyzerId = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (string.IsNullOrWhiteSpace(app))
        {
            throw new ArgumentException("app 不能为空", nameof(app));
        }

        if (string.IsNullOrWhiteSpace(stream))
        {
            throw new ArgumentException("stream 不能为空", nameof(stream));
        }

        if (IsOverlayStream(stream))
        {
            throw new ArgumentException($"不能对叠加流再叠加：{stream}（请指定原始流名）", nameof(stream));
        }

        if (_options.Http.Port <= 0)
        {
            throw new InvalidOperationException("叠加流需要本机 HTTP 端口（HTTP-FLV 取帧），当前 http.port=0");
        }

        if (_options.Rtmp.Port <= 0 || !_options.Protocol.EnableRtmp)
        {
            throw new InvalidOperationException("叠加流需要本机 RTMP 端口（推流回去），当前 rtmp.port=0 或 protocol.enable_rtmp=0");
        }

        var key = StreamKey.Create(vhost, app, stream);
        var source = _registry.Find(key.Vhost, key.App, key.Stream)
            ?? throw new InvalidOperationException($"源流不在线（请先推流或拉流代理）：{key}");

        var sourceWidth = source.Video.Width;
        var sourceHeight = source.Video.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new InvalidOperationException($"源流分辨率未知（还没收到关键帧）：{key}");
        }

        // libx264 + yuv420p 要求宽高为偶数
        var width = sourceWidth & ~1;
        var height = sourceHeight & ~1;
        if (width < 2 || height < 2)
        {
            throw new InvalidOperationException($"源流分辨率过小，无法编码：{sourceWidth}x{sourceHeight}");
        }

        var targetFps = fps is > 0 and <= 120
            ? fps.Value
            : source.Video.Fps is > 0 and <= 120
                ? (int)Math.Round(source.Video.Fps)
                : 25;

        var bitrate = bitrateKbps > 0 ? Math.Clamp(bitrateKbps, 200, 20000) : 1500;
        var scaleValue = Math.Clamp(labelScale, 1, 6);

        var inputUrl = BuildFlvUrl(key);
        var outputUrl = BuildRtmpUrl(key);

        lock (_gate)
        {
            if (_sessions.TryRemove(key, out var previous))
            {
                previous.Dispose();
            }

            var session = new OverlaySession(
                key,
                sourceWidth,
                sourceHeight,
                _results,
                _composer,
                analyzerId,
                _options.Env.ResolveFfmpegPath(),
                inputUrl,
                outputUrl,
                OverlayStreamName(key.Stream),
                width,
                height,
                targetFps,
                bitrate,
                scaleValue,
                DefaultResultMaxAge,
                _logger);

            try
            {
                session.Start();
            }
            catch
            {
                session.Dispose();
                throw;
            }

            _sessions[key] = session;
            _logger?.LogInformation(
                "结果叠加流启动 {Key} → {Output}（{Width}x{Height}@{Fps}fps，{Bitrate}kbps）",
                key,
                outputUrl,
                width,
                height,
                targetFps,
                bitrate);

            return session.Snapshot();
        }
    }

    /// <summary>停止一路叠加流；未在运行返回 false。</summary>
    public bool Stop(string vhost, string app, string stream)
    {
        var key = StreamKey.Create(vhost, app, stream);

        OverlaySession? session;
        lock (_gate)
        {
            if (!_sessions.TryRemove(key, out session))
            {
                return false;
            }
        }

        session.Dispose();
        _logger?.LogInformation("结果叠加流停止 {Key}", key);
        return true;
    }

    public int StopAll()
    {
        var count = 0;
        foreach (var key in _sessions.Keys.ToArray())
        {
            if (Stop(key.Vhost, key.App, key.Stream))
            {
                count++;
            }
        }

        return count;
    }

    public OverlayStatus? Get(string vhost, string app, string stream) =>
        _sessions.TryGetValue(StreamKey.Create(vhost, app, stream), out var session) ? session.Snapshot() : null;

    public IReadOnlyList<OverlayStatus> List(string vhost = "", string app = "", string stream = "") =>
        _sessions.Values
            .Select(session => session.Snapshot())
            .Where(item => Matches(item.Vhost, vhost) && Matches(item.App, app) && Matches(item.Stream, stream))
            .OrderBy(item => item.StartedAt)
            .ToArray();

    OverlayStatus IOverlayController.Start(OverlayRequest request) =>
        Start(request.Vhost, request.App, request.Stream, request.Fps, request.BitrateKbps, request.LabelScale, request.AnalyzerId);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopAll();
    }

    private static bool Matches(string value, string filter) =>
        filter.Length == 0 || string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);

    private string BuildFlvUrl(StreamKey key)
    {
        var prefix = key.Vhost == StreamKey.DefaultVhost ? string.Empty : "/" + key.Vhost;
        return $"http://127.0.0.1:{_options.Http.Port}{prefix}/{key.App}/{key.Stream}.live.flv";
    }

    private string BuildRtmpUrl(StreamKey key)
    {
        var url = $"rtmp://127.0.0.1:{_options.Rtmp.Port}/{key.App}/{OverlayStreamName(key.Stream)}";
        return key.Vhost == StreamKey.DefaultVhost
            ? url
            : $"{url}?vhost={Uri.EscapeDataString(key.Vhost)}";
    }
}

/// <summary>一路叠加流的 ffmpeg 解码 → 绘制 → 编码管线。</summary>
internal sealed class OverlaySession : IDisposable
{
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    private readonly StreamKey _key;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly OverlayResultCache _results;
    private readonly OverlayComposer _composer;
    private readonly string? _analyzerId;
    private readonly string _ffmpegPath;
    private readonly string _inputUrl;
    private readonly string _outputUrl;
    private readonly string _overlayStream;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _bitrateKbps;
    private readonly int _frameSize;
    private readonly TimeSpan _resultMaxAge;
    private readonly ILogger? _logger;

    private readonly Process _decoder = new();
    private readonly Process _encoder = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stderrLock = new();
    private readonly Dictionary<string, string> _lastStderr = new(StringComparer.Ordinal);

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private Task? _pump;
    private long _frames;
    private volatile OverlayState _state = OverlayState.Starting;
    private string? _error;
    private int _disposed;

    public OverlaySession(
        StreamKey key,
        int sourceWidth,
        int sourceHeight,
        OverlayResultCache results,
        OverlayComposer composer,
        string? analyzerId,
        string ffmpegPath,
        string inputUrl,
        string outputUrl,
        string overlayStream,
        int width,
        int height,
        int fps,
        int bitrateKbps,
        int labelScale,
        TimeSpan resultMaxAge,
        ILogger? logger)
    {
        _key = key;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _results = results;
        _composer = composer;
        _analyzerId = analyzerId;
        _ffmpegPath = ffmpegPath;
        _inputUrl = inputUrl;
        _outputUrl = outputUrl;
        _overlayStream = overlayStream;
        _width = width;
        _height = height;
        _fps = fps;
        _bitrateKbps = bitrateKbps;
        _frameSize = checked(width * height * 3);
        _resultMaxAge = resultMaxAge;
        _logger = logger;
    }

    public void Start()
    {
        StartProcess(_decoder, BuildDecoderArguments(), redirectStandardInput: false, redirectStandardOutput: true, "decoder");

        try
        {
            StartProcess(_encoder, BuildEncoderArguments(), redirectStandardInput: true, redirectStandardOutput: false, "encoder");
        }
        catch
        {
            KillProcess(_decoder);
            throw;
        }

        _state = OverlayState.Running;
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    public OverlayStatus Snapshot()
    {
        var error = CurrentError();

        return new OverlayStatus(
            _key.Vhost,
            _key.App,
            _key.Stream,
            _overlayStream,
            _inputUrl,
            _outputUrl,
            _state,
            _width,
            _height,
            _fps,
            _bitrateKbps,
            Interlocked.Read(ref _frames),
            _startedAt,
            Math.Max(0, (DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds),
            error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();

        try
        {
            _pump?.Wait(ExitGrace);
        }
        catch (AggregateException)
        {
        }

        KillProcess(_decoder);
        KillProcess(_encoder);

        _state = OverlayState.Stopped;
        _decoder.Dispose();
        _encoder.Dispose();
        _cts.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[_frameSize];
        var stride = _width * 3;

        try
        {
            var stdout = _decoder.StandardOutput.BaseStream;
            var stdin = _encoder.StandardInput.BaseStream;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stdout
                    .ReadAtLeastAsync(buffer.AsMemory(0, _frameSize), _frameSize, throwOnEndOfStream: false, cancellationToken)
                    .ConfigureAwait(false);

                if (read < _frameSize)
                {
                    break;
                }

                Draw(buffer, stride);

                await stdin.WriteAsync(buffer.AsMemory(0, _frameSize), cancellationToken).ConfigureAwait(false);
                await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _frames);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError($"叠加流管线异常：{exception.Message}");
            _logger?.LogWarning(exception, "结果叠加流管线异常 {Key}", _key);
        }
        finally
        {
            await ShutdownProcessesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Draw(byte[] buffer, int stride)
    {
        var results = _results.List(_key);
        if (results.Count == 0)
        {
            return;
        }

        // 与解码管线共用同一块缓冲（RgbImage 只是包装，不拷贝），渲染器直接写回
        var image = new RgbImage(buffer, _width, _height, stride);
        foreach (var result in results)
        {
            if (_analyzerId is { Length: > 0 } && !string.Equals(result.AnalyzerId, _analyzerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (result.Width != _sourceWidth || result.Height != _sourceHeight)
            {
                continue;
            }

            if (DateTimeOffset.UtcNow - result.At > _resultMaxAge)
            {
                continue;
            }

            _composer.Render(image, result);
        }
    }

    private async Task ShutdownProcessesAsync(CancellationToken cancellationToken)
    {
        // 先停解码，避免继续往已关闭的管道写
        KillProcess(_decoder);

        try
        {
            _encoder.StandardInput.BaseStream.Close();
        }
        catch (Exception)
        {
        }

        await WaitForExitAsync(_encoder).ConfigureAwait(false);
        await WaitForExitAsync(_decoder).ConfigureAwait(false);

        var cancelled = cancellationToken.IsCancellationRequested;
        var decoderExit = TryGetExitCode(_decoder);
        var encoderExit = TryGetExitCode(_encoder);

        if (cancelled)
        {
            _state = OverlayState.Stopped;
            return;
        }

        if (CurrentError() is null && decoderExit is > 0)
        {
            SetError($"解码进程退出（exit={decoderExit}）：{LastStderr("decoder")}");
        }

        if (CurrentError() is null && encoderExit is > 0)
        {
            SetError($"编码进程退出（exit={encoderExit}）：{LastStderr("encoder")}");
        }

        if (CurrentError() is null && Interlocked.Read(ref _frames) == 0)
        {
            SetError("源流没有输出视频帧（可能已停止或解码失败）");
        }

        var error = CurrentError();
        _state = error is null ? OverlayState.Stopped : OverlayState.Failed;
        _logger?.LogInformation(
            "结果叠加流结束 {Key}：state={State} frames={Frames} error={Error}",
            _key,
            _state,
            Interlocked.Read(ref _frames),
            error ?? "-");
    }

    private async Task WaitForExitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                using var timeout = new CancellationTokenSource(ExitGrace);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            KillProcess(process);
        }
    }

    private void SetError(string message)
    {
        lock (_stderrLock)
        {
            _error ??= message;
        }
    }

    private string? CurrentError()
    {
        lock (_stderrLock)
        {
            return _error;
        }
    }

    private string LastStderr(string tag)
    {
        lock (_stderrLock)
        {
            return _lastStderr.TryGetValue(tag, out var line) && line.Length > 0 ? line : "无 stderr 输出";
        }
    }

    private void StartProcess(Process process, IEnumerable<string> arguments, bool redirectStandardInput, bool redirectStandardOutput, string tag)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = redirectStandardOutput,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        process.StartInfo = startInfo;

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"无法启动 ffmpeg（{_ffmpegPath}）：{exception.Message}", exception);
        }

        _ = Task.Run(() => DrainStderrAsync(process, tag));
    }

    private async Task DrainStderrAsync(Process process, string tag)
    {
        try
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                line = line.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                lock (_stderrLock)
                {
                    _lastStderr[tag] = line;
                }

                _logger?.LogDebug("结果叠加流 ffmpeg[{Tag}] {Line}", tag, line);
            }
        }
        catch (Exception)
        {
            // 进程被杀时读 stderr 会抛异常，忽略
        }
    }

    private IEnumerable<string> BuildDecoderArguments()
    {
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "error";
        yield return "-nostdin";
        yield return "-fflags";
        yield return "nobuffer";
        yield return "-i";
        yield return _inputUrl;
        yield return "-an";
        yield return "-sn";
        yield return "-vf";
        yield return string.Create(
            CultureInfo.InvariantCulture,
            $"fps={_fps},scale={_width}:{_height}:flags=fast_bilinear");
        yield return "-f";
        yield return "rawvideo";
        yield return "-pix_fmt";
        yield return "rgb24";
        yield return "pipe:1";
    }

    private IEnumerable<string> BuildEncoderArguments()
    {
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "error";
        yield return "-nostdin";
        yield return "-f";
        yield return "rawvideo";
        yield return "-pixel_format";
        yield return "rgb24";
        yield return "-video_size";
        yield return string.Create(CultureInfo.InvariantCulture, $"{_width}x{_height}");
        yield return "-framerate";
        yield return _fps.ToString(CultureInfo.InvariantCulture);
        yield return "-i";
        yield return "pipe:0";
        yield return "-an";
        yield return "-c:v";
        yield return "libx264";
        yield return "-preset";
        yield return "veryfast";
        yield return "-tune";
        yield return "zerolatency";
        yield return "-pix_fmt";
        yield return "yuv420p";
        yield return "-g";
        yield return (_fps * 2).ToString(CultureInfo.InvariantCulture);
        yield return "-keyint_min";
        yield return _fps.ToString(CultureInfo.InvariantCulture);
        yield return "-sc_threshold";
        yield return "0";
        yield return "-b:v";
        yield return $"{_bitrateKbps}k";
        yield return "-maxrate";
        yield return $"{_bitrateKbps}k";
        yield return "-bufsize";
        yield return $"{_bitrateKbps * 2}k";
        yield return "-f";
        yield return "flv";
        yield return _outputUrl;
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
