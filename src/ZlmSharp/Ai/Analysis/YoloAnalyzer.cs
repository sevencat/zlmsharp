using System.Collections.Concurrent;
using ZlmSharp.Core;

namespace ZlmSharp.Ai.Analysis;

/// <summary>YOLO 作为通用分析层的一个实现：检测 + IoU 跟踪，输出 <see cref="AnalysisKind.Box"/> 图元。</summary>
public sealed class YoloAnalyzer : IStreamAnalyzer
{
    public const string AnalyzerId = "yolo";

    private readonly YoloDetectorService _detector;
    private readonly ConcurrentDictionary<string, TrackerState> _trackers = new(StringComparer.Ordinal);

    public YoloAnalyzer(YoloDetectorService detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detector = detector;
    }

    public string Id => AnalyzerId;

    public string DisplayName => "YOLO 目标检测";

    public bool Enabled => _detector.Enabled;

    public async Task<AnalysisResult> AnalyzeAsync(FrameSample frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        try
        {
            var result = await _detector.DetectAsync(frame.Image, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var tracked = GetTracker(frame.Key, result.Width, result.Height).Update(result.Objects);

            return new AnalysisResult(
                AnalyzerId,
                frame.Key,
                DateTimeOffset.Now,
                result.ElapsedMs,
                result.Width,
                result.Height,
                tracked.Select(ToPrimitive).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AnalysisResult(
                AnalyzerId,
                frame.Key,
                DateTimeOffset.Now,
                0,
                0,
                0,
                [],
                exception.Message);
        }
    }

    /// <summary>把定时检测的快照映射成通用结果（保持 <c>getYoloResult</c> 与 <c>analyzeResult</c> 同源）。</summary>
    public static AnalysisResult FromSnapshot(YoloSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new AnalysisResult(
            AnalyzerId,
            StreamKey.Create(snapshot.Vhost, snapshot.App, snapshot.Stream),
            snapshot.At,
            snapshot.ElapsedMs,
            snapshot.Width,
            snapshot.Height,
            snapshot.Objects.Select(ToPrimitive).ToArray(),
            snapshot.Error);
    }

    private static AnalysisPrimitive ToPrimitive(YoloObject item) => new(
        AnalysisKind.Box,
        item.ClassName,
        item.Confidence,
        item.TrackId == 0 ? null : (int)item.TrackId,
        item.X,
        item.Y,
        item.Width,
        item.Height,
        null,
        null,
        new Dictionary<string, string> { ["classId"] = item.ClassId.ToString() });

    private ObjectTracker GetTracker(StreamKey key, int width, int height)
    {
        var id = $"{key.Vhost}/{key.App}/{key.Stream}";
        if (_trackers.TryGetValue(id, out var state) && state.Width == width && state.Height == height)
        {
            return state.Tracker;
        }

        var tracker = new ObjectTracker();
        _trackers[id] = new TrackerState(tracker, width, height);
        return tracker;
    }

    private sealed record TrackerState(ObjectTracker Tracker, int Width, int Height);
}
