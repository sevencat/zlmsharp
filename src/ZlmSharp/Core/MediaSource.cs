namespace ZlmSharp.Core;

public sealed class MediaSourceOptions
{
    /// <summary>环形缓冲最多保留帧数（音视频合计，含 AAC 小帧）。</summary>
    public int MaxFrameCount { get; init; } = 2000;

    /// <summary>
    /// 环形缓冲按时间裁剪（毫秒），0 表示只按帧数裁剪。
    /// 必须大于常见长 GOP：摄像机/云直播源的关键帧间隔可能到 8~10 秒，
    /// 缓冲短于一个 GOP 会导致缓冲里找不到关键帧，新观众无法解码。
    /// </summary>
    public int MaxFrameMs { get; init; } = 15000;

    /// <summary>
    /// 每个观看者队列的帧数上限。需容纳一个完整长 GOP 的全部音视频帧
    /// （8 秒 @25fps 视频 200 帧 + AAC 48kHz 375 帧 ≈ 575），否则补发时就会丢帧。
    /// </summary>
    public int ReaderQueueCapacity { get; init; } = 1024;

    /// <summary>
    /// 直播播放端新观众补发 GOP 的时间窗口（毫秒），0 = 补发完整 GOP。
    /// 补发历史越长，播放器一开始就落后越多（延迟 ≈ 相机 GOP 长度）；
    /// 设小（如 1000）可降低常驻延迟，代价是窗口内没有关键帧时要等下一个关键帧才出画。
    /// 录制/转推不受影响，始终拿完整 GOP。
    /// </summary>
    public int GopCacheMs { get; init; }

    /// <summary>
    /// 直播播放端队列积压超过该帧数时，丢弃积压并跳到最近的关键帧（追赶实时），0 = 不追赶。
    /// 只在播放端网络/解码变慢时生效，避免延迟随积压无限累积（队列满时默认会丢新帧、延迟只增不减）。
    /// </summary>
    public int CatchUpPendingFrames { get; init; }
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

    public MediaSource(StreamKey key, string schema, MediaSourceOptions? options = null, string? originType = null)
    {
        Key = key;
        Schema = schema;
        OriginType = string.IsNullOrEmpty(originType) ? schema : originType;
        _options = options ?? new MediaSourceOptions();
    }

    public StreamKey Key { get; }

    /// <summary>流类型/业务类型，如 live、record（对应 URL 中的 schema）。</summary>
    public string Schema { get; }

    /// <summary>来源类型（<c>getMediaList.originType</c>）：推流等于 schema，拉流代理为 <c>proxy</c>。</summary>
    public string OriginType { get; }

    /// <summary>创建时间，用于 API 展示存活时长。</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public MediaTrack Video { get; } = new(TrackType.Video);

    public MediaTrack Audio { get; } = new(TrackType.Audio);

    public DateTime LastFrameAt { get; private set; } = DateTime.UtcNow;

    public long LastDts { get; private set; }

    /// <summary>最后一个观看者离开的时间；从未有人观看时为 null。</summary>
    public DateTime? LastReaderLeftAt { get; private set; }

    /// <summary>无人观看的起点：末个读者离开时间，没有读者时回落到创建时间。</summary>
    public DateTime IdleSince => LastReaderLeftAt ?? CreatedAt;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>所有观看者队列当前累计丢弃的帧数（慢客户端指标）。</summary>
    public long TotalDroppedFrames
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                foreach (var sink in _sinks.Values)
                {
                    total += sink.DroppedFrames;
                }

                return total;
            }
        }
    }

    /// <summary>所有观看者队列当前积压的帧数。</summary>
    public long TotalPendingFrames
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                foreach (var sink in _sinks.Values)
                {
                    total += sink.PendingCount;
                }

                return total;
            }
        }
    }

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
    /// <param name="livePlayback">
    /// true = 直播播放端：补发窗口受 <see cref="MediaSourceOptions.GopCacheMs"/> 限制，
    /// 且积压超限时会丢弃积压追赶实时。录制/转推应传 false，始终拿完整 GOP。
    /// </param>
    public MediaSink CreateReader(bool livePlayback = false)
    {
        var sink = new MediaSink(this, _options.ReaderQueueCapacity)
        {
            Id = Interlocked.Increment(ref _nextSinkId),
            AllowCatchUp = livePlayback,
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

        var gop = SnapshotGop(livePlayback ? _options.GopCacheMs : 0);
        if (gop.Count == 0 && livePlayback)
        {
            // 窗口内没有关键帧：丢掉注册瞬间已排队的帧，从下一个关键帧开始发，
            // 否则观众会先收到一串没有参考帧的 P 帧（花屏）
            sink.DropBacklog();
            sink.WaitKeyFrame();
        }

        foreach (var frame in gop)
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
            if (count == 0)
            {
                // 从这一刻开始计入「无人观看」时长
                LastReaderLeftAt = DateTime.UtcNow;
            }
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
            var catchUp = _options.CatchUpPendingFrames;
            foreach (var sink in sinks)
            {
                if (catchUp > 0 && sink.AllowCatchUp && sink.PendingCount >= catchUp)
                {
                    // 播放端跟不上：丢掉积压，从下一个关键帧续上，避免延迟一直累积
                    sink.DropBacklog();
                    sink.WaitKeyFrame();
                }

                sink.Push(frame);
            }
        }

        frame.Dispose();
    }

    /// <summary>取 GOP 快照（调用方负责 Dispose 每个帧）。</summary>
    /// <param name="maxAgeMs">
    /// 只接受最近该毫秒内的关键帧；关键帧更老时返回空（调用方等下一个关键帧）。0 = 不限制。
    /// 用于直播播放端压低补发历史带来的常驻延迟。
    /// </param>
    public IReadOnlyList<MediaFrame> SnapshotGop(int maxAgeMs = 0)
    {
        var result = new List<MediaFrame>();
        lock (_gate)
        {
            if (_ring.Count == 0)
            {
                return result;
            }

            var newestDts = _ring[^1].Dts;
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

            if (lastKeyIndex < 0)
            {
                // 缓冲内没有关键帧（源 GOP 比环形缓冲还长）：宁可补发空，让观众等下一个关键帧，
                // 也不要发一串没有参考帧的 P 帧——那会让播放器花屏/报 reference picture missing。
                return result;
            }

            if (maxAgeMs > 0 && newestDts - _ring[lastKeyIndex].Dts > maxAgeMs)
            {
                // 关键帧太老：补发它会让播放器一开始就落后一个 GOP，改由调用方等下一个关键帧
                return result;
            }

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
