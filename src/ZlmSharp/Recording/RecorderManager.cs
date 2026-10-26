using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;
using ZlmSharp.Core;

namespace ZlmSharp.Recording;

/// <summary>
/// 录像管理：<c>startRecord</c> 按流订阅并写 MP4/FLV，<c>max_second</c> 切片；
/// <c>protocol.enable_mp4 / enable_flv_record</c> 打开时由 <see cref="RecorderAutoStartService"/> 自动录。
/// </summary>
public sealed class RecorderManager : IDisposable
{
    private readonly MediaSourceRegistry _registry;
    private readonly AppOptions _options;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<StreamKey, RecorderSession> _sessions = new();
    private readonly ConcurrentDictionary<StreamKey, RecordPlan> _desired = new();
    private readonly object _gate = new();
    private int _disposed;

    public RecorderManager(MediaSourceRegistry registry, AppOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        _registry = registry;
        _options = options;
        _logger = logger;
    }

    /// <summary>开始录像；同一路流已在录会先停掉旧的再重开。会记住该请求，断流后自动续录。</summary>
    /// <exception cref="InvalidOperationException">流不在线或还没有参数集。</exception>
    public RecordingStatus Start(StreamKey key, RecordFormat formats, int? maxSeconds = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (formats == RecordFormat.None)
        {
            formats = RecordFormat.Mp4;
        }

        var source = _registry.Find(key.Vhost, key.App, key.Stream)
            ?? throw new InvalidOperationException($"流不在线：{key}");

        var seconds = ResolveSeconds(maxSeconds);
        var status = StartCore(key, source, formats, seconds);

        // 记录「期望录制」，断流后由 EnsureDesiredSessions 自动重开
        _desired[key] = new RecordPlan(formats, seconds);
        return status;
    }

    /// <summary>
    /// 让所有「期望录制」的流保持在录状态：断流导致的 Stopped/Failed 会话在流重新上线后自动续录。
    /// 由 <see cref="RecorderAutoStartService"/> 周期调用。
    /// </summary>
    public void EnsureDesiredSessions()
    {
        if (_desired.IsEmpty)
        {
            return;
        }

        foreach (var pair in _desired)
        {
            var key = pair.Key;
            var plan = pair.Value;

            if (_sessions.TryGetValue(key, out var existing) && existing.Snapshot().Recording)
            {
                continue;
            }

            if (!_registry.TryGet(key, out var source) || source is null || source.IsClosed || source.Video.Sps is null)
            {
                continue;
            }

            try
            {
                StartCore(key, source, plan.Formats, plan.Seconds);
                _logger?.LogInformation("录像续录 {Key}：formats={Formats}", key, plan.Formats);
            }
            catch (InvalidOperationException)
            {
                // 还没准备好，下一轮再试
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "录像续录失败 {Key}", key);
            }
        }
    }

    private int ResolveSeconds(int? maxSeconds) => Math.Clamp(
        maxSeconds is > 0
            ? maxSeconds.Value
            : _options.Protocol.Mp4MaxSecond > 0
                ? _options.Protocol.Mp4MaxSecond
                : 3600,
        10,
        86400);

    private RecordingStatus StartCore(StreamKey key, MediaSource source, RecordFormat formats, int seconds)
    {
        lock (_gate)
        {
            if (_sessions.TryRemove(key, out var previous))
            {
                previous.Dispose();
            }

            var session = new RecorderSession(
                key,
                source,
                formats,
                seconds,
                RecordPaths.ResolveRoot(_options),
                _logger,
                _options.Hls.SegDuration,
                _options.Hls.SegNum);

            session.Start();
            _sessions[key] = session;

            var status = session.Snapshot();
            _logger?.LogInformation(
                "录像启动 {Key}：formats={Formats} slice={Seconds}s mp4={Mp4} flv={Flv} hls={Hls}",
                key,
                formats,
                seconds,
                status.Mp4Path ?? "-",
                status.FlvPath ?? "-",
                status.HlsPath ?? "-");

            return status;
        }
    }

    public bool Stop(string vhost, string app, string stream)
    {
        var key = StreamKey.Create(vhost, app, stream);
        _desired.TryRemove(key, out _);

        RecorderSession? session;
        lock (_gate)
        {
            if (!_sessions.TryRemove(key, out session))
            {
                return false;
            }
        }

        session.Dispose();
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

    public RecordingStatus? Get(string vhost, string app, string stream) =>
        _sessions.TryGetValue(StreamKey.Create(vhost, app, stream), out var session)
            ? session.Snapshot()
            : null;

    public RecordingStatus? Get(StreamKey key) => _sessions.TryGetValue(key, out var session) ? session.Snapshot() : null;

    public IReadOnlyList<RecordingStatus> List(string vhost = "", string app = "", string stream = "") =>
        _sessions.Values
            .Select(session => session.Snapshot())
            .Where(item =>
                (vhost.Length == 0 || string.Equals(item.Vhost, vhost, StringComparison.OrdinalIgnoreCase)) &&
                (app.Length == 0 || string.Equals(item.App, app, StringComparison.OrdinalIgnoreCase)) &&
                (stream.Length == 0 || string.Equals(item.Stream, stream, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.StartedAt)
            .ToArray();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopAll();
        _desired.Clear();
    }

    /// <summary>用户显式请求过的录制参数，断流后按此续录。</summary>
    private readonly record struct RecordPlan(RecordFormat Formats, int Seconds);
}
