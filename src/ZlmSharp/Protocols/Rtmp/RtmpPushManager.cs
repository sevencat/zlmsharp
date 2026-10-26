using System.Collections.Concurrent;
using ZlmSharp.Core;

namespace ZlmSharp.Protocols.Rtmp;

public sealed record PushStatus(
    string Vhost,
    string App,
    string Stream,
    string Url,
    string State,
    long Frames,
    DateTimeOffset StartedAt,
    long ElapsedMs,
    string? Error)
{
    public bool Running => State is "connecting" or "running";
}

/// <summary>
/// RTMP 转推管理：每路本地流最多一个转推任务（目标 <c>rtmp://host/app/stream</c>），
/// 任务失败不会自动重连（避免对着错误地址空转），需要重新调用 <see cref="Start"/>。
/// </summary>
public sealed class RtmpPushManager : IDisposable
{
    private readonly MediaSourceRegistry _registry;
    private readonly RtmpLogHandler? _log;
    private readonly RtmpPushOptions? _options;
    private readonly ConcurrentDictionary<StreamKey, Session> _sessions = new();
    private readonly object _gate = new();
    private int _disposed;

    public RtmpPushManager(MediaSourceRegistry registry, RtmpLogHandler? log = null, RtmpPushOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _log = log;
        _options = options;
    }

    /// <summary>开始（或重启）一路转推。</summary>
    /// <exception cref="ArgumentException">目标不是合法 rtmp:// 地址。</exception>
    /// <exception cref="InvalidOperationException">源流不在线/还没有参数集。</exception>
    public PushStatus Start(string vhost, string app, string stream, Uri url)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        ArgumentNullException.ThrowIfNull(url);
        if (!RtmpPullClient.TryParseUrl(url, out _, out _, out _, out _))
        {
            throw new ArgumentException($"不是合法的 RTMP 推流地址: {url}", nameof(url));
        }

        var key = StreamKey.Create(vhost, app, stream);
        var source = _registry.Find(key.Vhost, key.App, key.Stream)
            ?? throw new InvalidOperationException($"源流不在线：{key}");

        lock (_gate)
        {
            if (_sessions.TryRemove(key, out var previous))
            {
                previous.Dispose();
            }

            if (source.Video.Sps is null || source.Video.Pps is null)
            {
                throw new InvalidOperationException($"源流还没有视频参数集（未收到关键帧）：{key}");
            }

            var session = new Session(key, source, url, _log, _options);
            session.Start();
            _sessions[key] = session;
            return session.Snapshot();
        }
    }

    public bool Stop(string vhost, string app, string stream)
    {
        Session? session;
        lock (_gate)
        {
            if (!_sessions.TryRemove(StreamKey.Create(vhost, app, stream), out session))
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

    public PushStatus? Get(string vhost, string app, string stream) =>
        _sessions.TryGetValue(StreamKey.Create(vhost, app, stream), out var session)
            ? session.Snapshot()
            : null;

    public IReadOnlyList<PushStatus> List(string vhost = "", string app = "", string stream = "") =>
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
    }

    private sealed class Session : IDisposable
    {
        private readonly StreamKey _key;
        private readonly MediaSource _source;
        private readonly Uri _url;
        private readonly RtmpLogHandler? _log;
        private readonly RtmpPushOptions? _options;
        private readonly CancellationTokenSource _cts = new();
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

        private Task? _task;
        private long _frames;
        private long _firstDts = -1;
        private long _lastDts = -1;
        private volatile string _state = "connecting";
        private string? _error;
        private int _disposed;

        public Session(StreamKey key, MediaSource source, Uri url, RtmpLogHandler? log, RtmpPushOptions? options)
        {
            _key = key;
            _source = source;
            _url = url;
            _log = log;
            _options = options;
        }

        public void Start() => _task = Task.Run(RunAsync);

        public PushStatus Snapshot()
        {
            var elapsed = (long)Math.Max(0, (DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds);
            var duration = _firstDts >= 0 && _lastDts >= _firstDts ? _lastDts - _firstDts : 0;

            return new PushStatus(
                _key.Vhost,
                _key.App,
                _key.Stream,
                _url.ToString(),
                _state,
                Interlocked.Read(ref _frames),
                _startedAt,
                elapsed > 0 ? elapsed : duration,
                _error);
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
                _task?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
            }

            _state = "stopped";
            _cts.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                _state = "running";
                await RtmpPushClient.RunAsync(
                        _url,
                        _source,
                        _options,
                        onFrame: frame =>
                        {
                            if (_firstDts < 0)
                            {
                                _firstDts = frame.Dts;
                            }

                            _lastDts = frame.Dts;
                            Interlocked.Increment(ref _frames);
                        },
                        log: _log,
                        cancellationToken: _cts.Token)
                    .ConfigureAwait(false);

                _state = "stopped";
            }
            catch (OperationCanceledException)
            {
                _state = "stopped";
            }
            catch (Exception exception)
            {
                _error = exception.Message;
                _state = "failed";
                _log?.Invoke(RtmpLogLevel.Warning, $"RTMP 转推失败 {_key} → {_url}：{exception.Message}");
            }
        }
    }
}
