namespace ZlmSharp.Ai;

/// <summary>
/// 轻量 IoU 目标跟踪：按类别做贪心匹配，给检测框分配跨帧稳定的 <see cref="YoloObject.TrackId"/>。
/// </summary>
/// <remarks>
/// 只做「同类别 + IoU ≥ 阈值」的贪心匹配（按 IoU 从大到小占用），不做卡尔曼/匈牙利，
/// 目的是让叠加标签有稳定序号、减少闪烁，不用于轨迹分析。
/// 实例不是线程安全的：一路流一个实例（<see cref="StreamYoloMonitor"/> 按流持有）。
/// </remarks>
public sealed class ObjectTracker
{
    /// <summary>默认 IoU 匹配阈值。</summary>
    public const float DefaultIouThreshold = 0.3f;

    /// <summary>默认允许连续丢失的检测次数（超过后丢弃轨迹，ID 不复用）。</summary>
    public const int DefaultMaxMissed = 5;

    private readonly float _iouThreshold;
    private readonly int _maxMissed;
    private readonly List<Track> _tracks = [];

    private long _nextId;

    public ObjectTracker(float iouThreshold = DefaultIouThreshold, int maxMissed = DefaultMaxMissed)
    {
        if (iouThreshold is <= 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(iouThreshold), "IoU 阈值必须在 (0, 1] 之间");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxMissed, 1);

        _iouThreshold = iouThreshold;
        _maxMissed = maxMissed;
    }

    /// <summary>当前仍在容忍期内的轨迹数。</summary>
    public int Count => _tracks.Count;

    /// <summary>
    /// 用一帧检测结果更新轨迹，返回带 <see cref="YoloObject.TrackId"/> 的检测框（顺序与入参一致）。
    /// </summary>
    public IReadOnlyList<YoloObject> Update(IReadOnlyList<YoloObject> detections)
    {
        ArgumentNullException.ThrowIfNull(detections);

        if (detections.Count == 0)
        {
            AgeTracks();
            return [];
        }

        var assigned = new long[detections.Count];
        var candidates = new List<Candidate>();

        for (var detection = 0; detection < detections.Count; detection++)
        {
            for (var track = 0; track < _tracks.Count; track++)
            {
                if (_tracks[track].Box.ClassId != detections[detection].ClassId)
                {
                    continue;
                }

                var iou = IntersectionOverUnion(_tracks[track].Box, detections[detection]);
                if (iou >= _iouThreshold)
                {
                    candidates.Add(new Candidate(detection, track, iou));
                }
            }
        }

        candidates.Sort(static (left, right) => right.Iou.CompareTo(left.Iou));

        var detectionMatched = new bool[detections.Count];
        var trackMatched = new bool[_tracks.Count];

        foreach (var candidate in candidates)
        {
            if (detectionMatched[candidate.Detection] || trackMatched[candidate.Track])
            {
                continue;
            }

            detectionMatched[candidate.Detection] = true;
            trackMatched[candidate.Track] = true;

            var track = _tracks[candidate.Track];
            track.Box = detections[candidate.Detection];
            track.Missed = 0;
            assigned[candidate.Detection] = track.Id;
        }

        AgeTracks(trackMatched);

        for (var detection = 0; detection < detections.Count; detection++)
        {
            if (detectionMatched[detection])
            {
                continue;
            }

            var track = new Track(++_nextId, detections[detection]);
            _tracks.Add(track);
            assigned[detection] = track.Id;
        }

        var result = new List<YoloObject>(detections.Count);
        for (var detection = 0; detection < detections.Count; detection++)
        {
            result.Add(detections[detection] with { TrackId = assigned[detection] });
        }

        return result;
    }

    /// <summary>清空轨迹并重置 ID 计数（视频源重启/分辨率变化时调用）。</summary>
    public void Reset()
    {
        _tracks.Clear();
        _nextId = 0;
    }

    private void AgeTracks(bool[]? matched = null)
    {
        for (var track = _tracks.Count - 1; track >= 0; track--)
        {
            if (matched is not null && matched[track])
            {
                continue;
            }

            _tracks[track].Missed++;
            if (_tracks[track].Missed > _maxMissed)
            {
                _tracks.RemoveAt(track);
            }
        }
    }

    private static float IntersectionOverUnion(YoloObject left, YoloObject right)
    {
        var x1 = MathF.Max(left.X, right.X);
        var y1 = MathF.Max(left.Y, right.Y);
        var x2 = MathF.Min(left.X + left.Width, right.X + right.Width);
        var y2 = MathF.Min(left.Y + left.Height, right.Y + right.Height);

        if (x2 <= x1 || y2 <= y1)
        {
            return 0f;
        }

        var intersection = (x2 - x1) * (y2 - y1);
        var union = (left.Width * left.Height) + (right.Width * right.Height) - intersection;
        return union <= 0f ? 0f : intersection / union;
    }

    private readonly record struct Candidate(int Detection, int Track, float Iou);

    private sealed class Track(long id, YoloObject box)
    {
        public long Id { get; } = id;

        public YoloObject Box { get; set; } = box;

        public int Missed { get; set; }
    }
}
