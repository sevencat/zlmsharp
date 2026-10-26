using System.Collections.Concurrent;
using ZlmSharp.Core;

namespace ZlmSharp.Protocols.Rtmp;

public sealed record RtmpProxyInfo(StreamKey Key, string Url, DateTimeOffset StartedAt);

/// <summary>
/// RTMP 拉流代理：把远端 RTMP 拉进本地 <see cref="MediaSource"/>。
/// 之后本地就能用 RTMP / HTTP-FLV / WS-FLV / RTSP 正常播放，也能被 YOLO 定时抽帧检测。
/// 断线自动重连（2s 间隔），直到调用 <see cref="Stop"/> 或 <see cref="DisposeAsync"/>。
/// </summary>
public sealed class RtmpPullProxyManager : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly MediaSourceRegistry _registry;
    private readonly RtmpLogHandler? _log;
    private readonly ConcurrentDictionary<StreamKey, ProxyEntry> _entries = new();
    private readonly CancellationTokenSource _shutdown = new();

    public RtmpPullProxyManager(MediaSourceRegistry registry, RtmpLogHandler? log = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _log = log;
    }

    public IReadOnlyList<RtmpProxyInfo> List() =>
        _entries.Values.Select(entry => new RtmpProxyInfo(entry.Key, entry.Url, entry.StartedAt)).ToArray();

    public bool IsPulling(StreamKey key) => _entries.ContainsKey(key);

    /// <summary>开始（或重启）一路拉流代理，返回本地流标识。</summary>
    /// <exception cref="ArgumentException">参数或 URL 非法。</exception>
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

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !RtmpPullClient.TryParseUrl(uri, out _, out _, out _, out _))
        {
            throw new ArgumentException($"url 必须是 rtmp:// 地址：{url}", nameof(url));
        }

        var key = StreamKey.Create(vhost, app, stream);
        Stop(key);

        var source = _registry.GetOrCreate(key, "rtmp");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var entry = new ProxyEntry(key, url, cts);
        _entries[key] = entry;
        entry.Task = Task.Run(() => PullLoopAsync(source, uri, cts.Token), CancellationToken.None);

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

    private async Task PullLoopAsync(MediaSource source, Uri uri, CancellationToken cancellationToken)
    {
        var options = new RtmpPullOptions();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RtmpPullClient.RunAsync(uri, source, options, _log, cancellationToken).ConfigureAwait(false);
                Log(RtmpLogLevel.Info, $"拉流结束，{RetryDelay.TotalSeconds} 秒后重连 {uri}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Log(RtmpLogLevel.Warning, $"拉流失败（{exception.Message}），{RetryDelay.TotalSeconds} 秒后重连 {uri}");
            }

            try
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
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
