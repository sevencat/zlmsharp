using ZlmSharp.Ai;

namespace ZlmSharp.Tests;

/// <summary>轻量 IoU 跟踪：跨帧 ID 稳定、类别隔离、丢失容忍与过期回收。</summary>
public class ObjectTrackerTests
{
    [Fact]
    public void Update_AssignsStableIdAcrossFrames()
    {
        var tracker = new ObjectTracker();
        var person = Object(0, 10, 10, 50, 50);

        var first = tracker.Update([person]);
        var second = tracker.Update([person with { X = 12, Y = 11 }]);

        Assert.Equal(1, Assert.Single(first).TrackId);
        Assert.Equal(1, Assert.Single(second).TrackId);
    }

    [Fact]
    public void Update_TwoObjects_GetDistinctIdsInInputOrder()
    {
        var tracker = new ObjectTracker();

        var result = tracker.Update([
            Object(0, 0, 0, 40, 40),
            Object(0, 200, 200, 40, 40),
        ]);

        Assert.Equal([1L, 2L], result.Select(item => item.TrackId).ToArray());
    }

    [Fact]
    public void Update_WithoutOverlap_AssignsNewId()
    {
        var tracker = new ObjectTracker();

        Assert.Equal(1, Assert.Single(tracker.Update([Object(0, 0, 0, 40, 40)])).TrackId);
        Assert.Equal(2, Assert.Single(tracker.Update([Object(0, 300, 300, 40, 40)])).TrackId);
    }

    [Fact]
    public void Update_DifferentClass_DoesNotMatch()
    {
        var tracker = new ObjectTracker();

        Assert.Equal(1, Assert.Single(tracker.Update([Object(0, 10, 10, 40, 40)])).TrackId);
        Assert.Equal(2, Assert.Single(tracker.Update([Object(2, 10, 10, 40, 40)])).TrackId);
    }

    [Fact]
    public void Update_ToleratesShortDetectionGap()
    {
        var tracker = new ObjectTracker(maxMissed: 3);

        Assert.Equal(1, Assert.Single(tracker.Update([Object(0, 10, 10, 40, 40)])).TrackId);
        Assert.Empty(tracker.Update([]));
        Assert.Empty(tracker.Update([]));

        // 丢失两帧后重新出现，仍是同一条轨迹
        Assert.Equal(1, Assert.Single(tracker.Update([Object(0, 10, 10, 40, 40)])).TrackId);
    }

    [Fact]
    public void Update_DropsTrackAfterMaxMissed()
    {
        var tracker = new ObjectTracker(maxMissed: 2);

        Assert.Equal(1, Assert.Single(tracker.Update([Object(0, 10, 10, 40, 40)])).TrackId);
        Assert.Empty(tracker.Update([]));
        Assert.Empty(tracker.Update([]));
        Assert.Equal(1, tracker.Count);
        Assert.Empty(tracker.Update([]));
        Assert.Equal(0, tracker.Count);

        // ID 不复用：重新出现是新轨迹
        Assert.Equal(2, Assert.Single(tracker.Update([Object(0, 10, 10, 40, 40)])).TrackId);
    }

    [Fact]
    public void Update_EmptyDetections_ReturnsEmpty()
    {
        var tracker = new ObjectTracker();
        Assert.Empty(tracker.Update([]));
    }

    [Fact]
    public void Reset_RestartsIdSequence()
    {
        var tracker = new ObjectTracker();
        Assert.Equal(1, Assert.Single(tracker.Update([Object(0, 0, 0, 40, 40)])).TrackId);

        tracker.Reset();
        Assert.Equal(0, tracker.Count);
        Assert.Equal(1, Assert.Single(tracker.Update([Object(0, 0, 0, 40, 40)])).TrackId);
    }

    [Fact]
    public void Constructor_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectTracker(iouThreshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectTracker(iouThreshold: 1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectTracker(maxMissed: 0));
    }

    private static YoloObject Object(int classId, float x, float y, float width, float height) =>
        new(classId, "obj", 0.9f, x, y, width, height);
}
