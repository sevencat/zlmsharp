using System.Collections.Concurrent;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtsp;

namespace ZlmSharp.Protocols.Rtmp;

public sealed record RtmpProxyInfo(StreamKey Key, string Url, DateTimeOffset StartedAt);

public enum PullProtocol
{
    Rtmp,
    Rtsp,
}

/// <summary>
/// 拉流代理：把远端 RTMP / RTSP 拉进本地 <see cref="MediaSource"/>（<c>originType=proxy</c>）。
/// 之后本地就能用 RTMP / HTTP-FLV / WS-FLV / RTSP 正常播放，也能被 YOLO 检测。
/// 断线自动重连，直到调用 <see cref="Stop"/> 或 <see cref="DisposeAsync"/>。
/// </summary>
public sealed class RtmpPullProxyManager : IAsyncDisposable
{
    private readonly MediaSourceRegistry _registry;
    private readonly RtmpLogHandler? _log;
    private readonly RtspLogHandler? _rtspLog;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _retryDelay;
    private readonly ConcurrentDictionary<StreamKey, ProxyEntry> _entries = new();
    private readonly CancellationTokenSource _shutdown = new();

    public RtmpPullProxyManager(
        MediaSourceRegistry registry,
        RtmpLogHandler? log = null,
        int maxConcurrency = 16,
        int retryDelayMs = 2000,
        RtspLogHandler? rtspLog = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _log = log;
        _rtspLog = rtspLog;
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, 256);
        _retryDelay = TimeSpan.FromMilliseconds(Math.Clamp(retryDelayMs, 200, 60_000));
    }

    public int Count => _entries.Count;

    public IReadOnlyList<RtmpProxyInfo> List() =>
        _entries.Values.Select(entry => new RtmpProxyInfo(entry.Key, entry.Url, entry.StartedAt)).ToArray();

    public bool IsPulling(StreamKey key) => _entries.ContainsKey(key);

    /// <summary>解析 <c>app/stream=url;vhost/app/stream=url</c> 形式的代理列表。</summary>
    public static IReadOnlyList<(StreamKey Key, string Url)> ParseList(string? list)
    {
        var result = new List<(StreamKey, string)>();
        if (string.IsNullOrWhiteSpace(list))
        {
            return result;
        }

        foreach (var item in list.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = item.Trim();
            var separator = trimmed.IndexOf('=');
            if (separator <= 0 || separator == trimmed.Length - 1)
            {
                continue;
            }

            var keyText = trimmed[..separator].Trim();
            var url = trimmed[(separator + 1)..].Trim();
            var parts = keyText.Split('/', StringSplitOptions.RemoveEmptyEntries);

            StreamKey key;
            if (parts.Length == 2)
            {
                key = StreamKey.Create(null, parts[0], parts[1]);
            }
            else if (parts.Length == 3)
            {
                key = StreamKey.Create(parts[0], parts[1], parts[2]);
            }
            else
            {
                continue;
            }

            result.Add((key, url));
        }

        return result;
    }

    /// <summary>开始（或重启）一路拉流代理，返回本地流标识。</summary>
    /// <exception cref="ArgumentException">参数或 URL 非法。</exception>
    /// <exception cref="InvalidOperationException">超过并发上限。</exception>
    public StreamKey Start(string? vhost, string app, string stream, string url)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            throw new ArgumentException("app 不能为空", nameof(app));
        }

        if (string.IsNullOrWhiteSpace(stream))
        {
            throw new ArgumentException("stream 不能为空", nameof(stream));
        }

        if (!TryResolve(url, out var uri, out var protocol))
        {
            throw new ArgumentException($"url 必须是 rtmp:// 或 rtsp:// 地址：{url}", nameof(url));
        }

        var key = StreamKey.Create(vhost, app, stream);
        var isRestart = _entries.ContainsKey(key);

        if (!isRestart && _entries.Count >= _maxConcurrency)
        {
            throw new InvalidOperationException($"拉流代理已达并发上限（{_maxConcurrency}）");
        }

        Stop(key);

        // schema 跟随源协议（与推流侧 GetOrCreate(key, "rtmp"/"rtsp") 保持一致）
        var schema = protocol == PullProtocol.Rtsp ? "rtsp" : "rtmp";
        var source = _registry.GetOrCreate(key, schema, originType: "proxy");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var entry = new ProxyEntry(key, url, cts);
        _entries[key] = entry;
        entry.Task = Task.Run(() => PullLoopAsync(source, uri, protocol, cts.Token), CancellationToken.None);

        Log(RtmpLogLevel.Info, $"开始拉流代理 {key} ← {url}");
        return key;
    }

    public bool Stop(StreamKey key)
    {
        if (!_entries.TryRemove(key, out var entry))
        {
            return false;
        }

        entry.Cts.Cancel();
        _ = DisposeEntryAsync(entry);
        _registry.Remove(key, out _);
        Log(RtmpLogLevel.Info, $"停止拉流代理 {key} ← {entry.Url}");
        return true;
    }

    public int StopAll()
    {
        var count = 0;
        foreach (var key in _entries.Keys.ToArray())
        {
            if (Stop(key))
            {
                count++;
            }
        }

        return count;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        var entries = _entries.Values.ToArray();
        _entries.Clear();

        foreach (var entry in entries)
        {
            entry.Cts.Cancel();
            _registry.Remove(entry.Key, out _);
        }

        foreach (var entry in entries)
        {
            await DisposeEntryAsync(entry).ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }

    private static bool TryResolve(string url, out Uri uri, out PullProtocol protocol)
    {
        uri = null!;
        protocol = PullProtocol.Rtmp;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (string.Equals(parsed.Scheme, "rtmp", StringComparison.OrdinalIgnoreCase) &&
            RtmpPullClient.TryParseUrl(parsed, out _, out _, out _, out _))
        {
            uri = parsed;
            protocol = PullProtocol.Rtmp;
            return true;
        }

        if (string.Equals(parsed.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase) &&
            RtspPullClient.TryParseUrl(parsed, out _, out _, out _, out _, out _))
        {
            uri = parsed;
            protocol = PullProtocol.Rtsp;
            return true;
        }

        return false;
    }

    private async Task PullLoopAsync(
        MediaSource source,
        Uri uri,
        PullProtocol protocol,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (protocol == PullProtocol.Rtsp)
                {
                    await RtspPullClient
                        .RunAsync(uri, source, new RtspPullOptions(), _rtspLog, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await RtmpPullClient
                        .RunAsync(uri, source, new RtmpPullOptions(), _log, cancellationToken)
                        .ConfigureAwait(false);
                }

                Log(RtmpLogLevel.Info, $"拉流结束，{_retryDelay.TotalSeconds} 秒后重连 {uri}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Log(RtmpLogLevel.Warning, $"拉流失败（{exception.Message}），{_retryDelay.TotalSeconds} 秒后重连 {uri}");
            }

            try
            {
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DisposeEntryAsync(ProxyEntry entry)
    {
        try
        {
            await entry.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or IOException)
        {
            Log(RtmpLogLevel.Debug, $"拉流任务停止：{exception.Message}");
        }
        finally
        {
            entry.Cts.Dispose();
        }
    }

    private void Log(RtmpLogLevel level, string message) => _log?.Invoke(level, message);

    private sealed class ProxyEntry
    {
        public ProxyEntry(StreamKey key, string url, CancellationTokenSource cts)
        {
            Key = key;
            Url = url;
            Cts = cts;
            StartedAt = DateTimeOffset.UtcNow;
        }

        public StreamKey Key { get; }

        public string Url { get; }

        public CancellationTokenSource Cts { get; }

        public DateTimeOffset StartedAt { get; }

        public Task Task { get; set; } = Task.CompletedTask;
    }
}
