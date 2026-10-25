namespace ZlmSharp.Core;

public sealed class MediaSourceOptions
{
    /// <summary>环形缓冲最多保留帧数。</summary>
    public int MaxFrameCount { get; init; } = 1000;

    /// <summary>环形缓冲按时间裁剪（毫秒），0 表示只按帧数裁剪。</summary>
    public int MaxFrameMs { get; init; } = 5000;

    /// <summary>每个观看者队列的帧数上限。</summary>
    public int ReaderQueueCapacity { get; init; } = 128;
}

/// <summary>
/// 一路流（vhost/app/stream）。持有环形缓冲 + GOP、向所有 <see cref="MediaSink"/> 广播帧。
/// 线程安全：AddFrame 可被任意线程调用；读者注册/注销与 Close 可并发。
/// </summary>
public sealed class MediaSource : IDisposable
{
    private readonly object _gate = new();
    private readonly List<MediaFrame> _ring = new();
    private readonly Dictionary<long, MediaSink> _sinks = new();
    private readonly MediaSourceOptions _options;

    private long _nextSinkId;
    private long _totalReaderCount;
    private int _closed;

    public MediaSource(StreamKey key, string schema, MediaSourceOptions? options = null)
    {
        Key = key;
        Schema = schema;
        _options = options ?? new MediaSourceOptions();
    }

    public StreamKey Key { get; }

    /// <summary>流类型/业务类型，如 live、record（对应 URL 中的 schema）。</summary>
    public string Schema { get; }

    public MediaTrack Video { get; } = new(TrackType.Video);

    public MediaTrack Audio { get; } = new(TrackType.Audio);

    public DateTime LastFrameAt { get; private set; } = DateTime.UtcNow;

    public long LastDts { get; private set; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>历史累计观看次数。</summary>
    public long TotalReaderCount => Volatile.Read(ref _totalReaderCount);

    public int ReaderCount
    {
        get
        {
            lock (_gate)
            {
                return _sinks.Count;
            }
        }
    }

    public int FrameCacheCount
    {
        get
        {
            lock (_gate)
            {
                return _ring.Count;
            }
        }
    }

    public event Action<MediaSource, int>? ReaderCountChanged;

    public event Action<MediaSource>? Closed;

    /// <summary>注册一个观看者，并立即补发 GOP（从关键帧起），保证秒开。</summary>
    public MediaSink CreateReader()
    {
        var sink = new MediaSink(this, _options.ReaderQueueCapacity)
        {
            Id = Interlocked.Increment(ref _nextSinkId),
        };

        int count;
        lock (_gate)
        {
            if (IsClosed)
            {
                sink.Close();
                ReaderCountChanged?.Invoke(this, 0);
                return sink;
            }

            _sinks[sink.Id] = sink;
            count = _sinks.Count;
        }

        Interlocked.Increment(ref _totalReaderCount);

        foreach (var frame in SnapshotGop())
        {
            sink.Push(frame);
            frame.Dispose();
        }

        ReaderCountChanged?.Invoke(this, count);
        return sink;
    }

    public bool RemoveReader(MediaSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        int count;
        lock (_gate)
        {
            if (!_sinks.Remove(sink.Id))
            {
                return false;
            }

            count = _sinks.Count;
        }

        sink.Close();
        ReaderCountChanged?.Invoke(this, count);
        return true;
    }

    /// <summary>写入一帧并广播；本方法不接管调用方的引用（末尾会 Dispose 一次）。</summary>
    public void AddFrame(MediaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        MediaSink[]? sinks = null;
        lock (_gate)
        {
            if (IsClosed)
            {
                frame.Dispose();
                return;
            }

            UpdateTracks(frame);
            LastFrameAt = DateTime.UtcNow;
            LastDts = frame.Dts;

            if (frame.TryAddRef())
            {
                _ring.Add(frame);
                TrimLocked();
            }

            if (_sinks.Count > 0)
            {
                sinks = new MediaSink[_sinks.Count];
                _sinks.Values.CopyTo(sinks, 0);
            }
        }

        if (sinks is not null)
        {
            foreach (var sink in sinks)
            {
                sink.Push(frame);
            }
        }

        frame.Dispose();
    }

    /// <summary>取 GOP 快照（调用方负责 Dispose 每个帧）。</summary>
    public IReadOnlyList<MediaFrame> SnapshotGop()
    {
        var result = new List<MediaFrame>();
        lock (_gate)
        {
            if (_ring.Count == 0)
            {
                return result;
            }

            var start = 0;
            var lastKeyIndex = -1;
            for (var index = _ring.Count - 1; index >= 0; index--)
            {
                var frame = _ring[index];
                if (frame.TrackType == TrackType.Video && frame.IsKeyFrame)
                {
                    lastKeyIndex = index;
                    break;
                }
            }

            if (lastKeyIndex >= 0)
            {
                start = lastKeyIndex;

                // 只把紧挨着关键帧的配置帧一起带上；更早的配置帧属于历史 GOP，不再补发
                for (var index = lastKeyIndex - 1; index >= 0; index--)
                {
                    var frame = _ring[index];
                    if (frame.TrackType == TrackType.Video && frame.IsConfig)
                    {
                        start = index;
                        continue;
                    }

                    break;
                }
            }

            for (var index = start; index < _ring.Count; index++)
            {
                var frame = _ring[index];
                if (frame.TryAddRef())
                {
                    result.Add(frame);
                }
            }
        }

        return result;
    }

    public void Close()
    {
        MediaSink[] sinks;
        MediaFrame[] frames;

        lock (_gate)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            sinks = new MediaSink[_sinks.Count];
            _sinks.Values.CopyTo(sinks, 0);
            _sinks.Clear();

            frames = _ring.ToArray();
            _ring.Clear();
        }

        foreach (var frame in frames)
        {
            frame.Dispose();
        }

        foreach (var sink in sinks)
        {
            sink.Close();
        }

        ReaderCountChanged?.Invoke(this, 0);
        Closed?.Invoke(this);
    }

    public void Dispose() => Close();

    private void UpdateTracks(MediaFrame frame)
    {
        if (frame.TrackType == TrackType.Video)
        {
            Video.UpdateFromFrame(frame);
        }
        else
        {
            Audio.UpdateFromFrame(frame);
        }
    }

    private void TrimLocked()
    {
        while (_ring.Count > Math.Max(1, _options.MaxFrameCount))
        {
            var oldest = _ring[0];
            _ring.RemoveAt(0);
            oldest.Dispose();
        }

        if (_options.MaxFrameMs <= 0)
        {
            return;
        }

        while (_ring.Count > 0 && LastDts - _ring[0].Dts > _options.MaxFrameMs)
        {
            var oldest = _ring[0];
            _ring.RemoveAt(0);
            oldest.Dispose();
        }
    }

    public override string ToString() => $"{Key} schema={Schema} readers={ReaderCount} frames={FrameCacheCount}";
}
