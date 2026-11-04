using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ZlmSharp.Core;

/// <summary>所有在线的流。发布者注册、观看者查找、空闲回收。</summary>
public sealed class MediaSourceRegistry
{
    private readonly ConcurrentDictionary<StreamKey, MediaSource> _sources = new();
    private readonly ILogger? _logger;

    public MediaSourceRegistry(ILogger? logger = null) => _logger = logger;

    public int Count => _sources.Count;

    public IEnumerable<MediaSource> Sources => _sources.Values;

    public MediaSource GetOrCreate(
        StreamKey key,
        string schema,
        Func<MediaSource>? factory = null,
        string? originType = null)
    {
        while (true)
        {
            if (_sources.TryGetValue(key, out var existing))
            {
                if (!existing.IsClosed)
                {
                    return existing;
                }

                // 已关闭的残留项：移除后重建
                _sources.TryRemove(new KeyValuePair<StreamKey, MediaSource>(key, existing));
                _logger?.LogDebug("清理已关闭的残留流 {Key}", key);
                continue;
            }

            var created = factory?.Invoke() ?? new MediaSource(key, schema, originType: originType);
            if (_sources.TryAdd(key, created))
            {
                return created;
            }

            created.Close();
        }
    }

    public bool TryGet(StreamKey key, out MediaSource? source) => _sources.TryGetValue(key, out source);

    public MediaSource? Find(string vhost, string app, string stream) =>
        _sources.TryGetValue(StreamKey.Create(vhost, app, stream), out var source) ? source : null;

    public bool Remove(StreamKey key, out MediaSource? source)
    {
        if (_sources.TryRemove(key, out source))
        {
            source.Close();
            _logger?.LogInformation(
                "关闭流 {Key}（schema={Schema}，来源={OriginType}）",
                key,
                source.Schema,
                source.OriginType);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 仅当该 key 当前登记的仍是 <paramref name="expected"/> 时才移除并关闭。
    /// 用于推流会话收尾：同一 key 的新推流者可能已经接管并创建了新实例，
    /// 按 key 无脑移除会把新推流者正在写的流摘掉（叠加/转推反复重启时最明显）。
    /// </summary>
    public bool Remove(StreamKey key, MediaSource expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        if (_sources.TryRemove(new KeyValuePair<StreamKey, MediaSource>(key, expected)))
        {
            expected.Close();
            // 调用方（推流会话收尾等）通常已记录更具体的结束日志，这里只在 Debug 留痕
            _logger?.LogDebug("移除流 {Key}（实例匹配）", key);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 关闭并移除无人观看且空闲超过 <paramref name="idleTimeout"/> 的流，返回移除数量。
    /// 「空闲」从最后一个读者离开算起；从未有人观看时从创建时间算起。
    /// <paramref name="predicate"/> 用于叠加额外条件（例如是否允许关闭正在推流的流）。
    /// </summary>
    public int CleanupIdle(TimeSpan idleTimeout, Func<MediaSource, bool>? predicate = null, DateTime? now = null)
    {
        var current = now ?? DateTime.UtcNow;
        var removed = 0;

        foreach (var pair in _sources)
        {
            var source = pair.Value;
            if (source.ReaderCount > 0 || current - source.IdleSince < idleTimeout)
            {
                continue;
            }

            if (predicate is not null && !predicate(source))
            {
                continue;
            }

            if (_sources.TryRemove(pair))
            {
                source.Close();
                removed++;
                _logger?.LogDebug("空闲回收流 {Key}（schema={Schema}）", source.Key, source.Schema);
            }
        }

        return removed;
    }
}
