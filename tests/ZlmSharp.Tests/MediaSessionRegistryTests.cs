using ZlmSharp.Core;

namespace ZlmSharp.Tests;

public class MediaSessionRegistryTests
{
    private static readonly StreamKey Key = StreamKey.Create("vhost", "live", "test");

    [Fact]
    public void Register_ThenUnregister_TracksCount()
    {
        var registry = new MediaSessionRegistry();
        var session = registry.Register("rtmp", MediaSessionType.Publisher, Key, "127.0.0.1", 1234, "ua", () => { });

        Assert.Equal(1, registry.Count);
        Assert.NotEmpty(session.Id);
        Assert.Contains(registry.Snapshot(), item => item.Id == session.Id);

        Assert.True(registry.Unregister(session.Id));
        Assert.Equal(0, registry.Count);
        Assert.False(registry.Unregister(session.Id));
        Assert.False(registry.Unregister(null));
    }

    [Fact]
    public void Kick_OnlyMatchesPredicate_AndInvokesCallbackOnce()
    {
        var registry = new MediaSessionRegistry();
        var callbacks = 0;
        var publisher = registry.Register(
            "rtsp",
            MediaSessionType.Publisher,
            Key,
            null,
            0,
            null,
            () => Interlocked.Increment(ref callbacks));
        var player = registry.Register(
            "http-flv",
            MediaSessionType.Player,
            Key,
            null,
            0,
            null,
            () => Interlocked.Increment(ref callbacks));

        var kicked = registry.Kick(session => session.Type == MediaSessionType.Player);

        Assert.Single(kicked);
        Assert.Equal(player.Id, kicked[0].Id);
        Assert.Equal(1, callbacks);

        var remaining = registry.Snapshot();
        Assert.Single(remaining);
        Assert.Equal(publisher.Id, remaining[0].Id);
    }

    [Fact]
    public void Snapshot_FiltersByKeyAndOrdersByCreatedAt()
    {
        var registry = new MediaSessionRegistry();
        var first = registry.Register("rtmp", MediaSessionType.Player, Key, null, 0, null, () => { });
        var second = registry.Register(
            "rtsp",
            MediaSessionType.Player,
            StreamKey.Create("vhost", "live", "other"),
            null,
            0,
            null,
            () => { });

        var all = registry.Snapshot();
        Assert.Equal(2, all.Count);
        Assert.Equal(first.Id, all[0].Id);
        Assert.Equal(second.Id, all[1].Id);

        var filtered = registry.Snapshot(session => session.Key.Stream == "test");
        Assert.Single(filtered);
        Assert.Equal(first.Id, filtered[0].Id);
    }
}
