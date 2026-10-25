using System.Collections.Concurrent;

namespace ZlmSharp.Core;

/// <summary>所有在线的流。发布者注册、观看者查找、空闲回收。</summary>
public sealed class MediaSourceRegistry
{
    private readonly ConcurrentDictionary<StreamKey, MediaSource> _sources = new();

    public int Count => _sources.Count;

    public IEnumerable<MediaSource> Sources => _sources.Values;

    public MediaSource GetOrCreate(StreamKey key, string schema, Func<MediaSource>? factory = null)
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
                continue;
            }

            var created = factory?.Invoke() ?? new MediaSource(key, schema);
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
            return true;
        }

        return false;
    }

    /// <summary>关闭并移除无人观看且超过空闲时间的流，返回移除数量。</summary>
    public int CleanupIdle(TimeSpan idleTimeout, DateTime? now = null)
    {
        var deadline = (now ?? DateTime.UtcNow) - idleTimeout;
        var removed = 0;

        foreach (var pair in _sources)
        {
            var source = pair.Value;
            if (source.ReaderCount > 0 || source.LastFrameAt > deadline)
            {
                continue;
            }

            if (_sources.TryRemove(pair))
            {
                source.Close();
                removed++;
            }
        }

        return removed;
    }
}
