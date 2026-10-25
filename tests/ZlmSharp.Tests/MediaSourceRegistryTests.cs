using ZlmSharp.Core;

namespace ZlmSharp.Tests;

public class MediaSourceRegistryTests
{
    private static readonly StreamKey Key = StreamKey.Create("vhost", "live", "test");

    [Fact]
    public void GetOrCreate_ReturnsSameInstance()
    {
        var registry = new MediaSourceRegistry();

        var first = registry.GetOrCreate(Key, "live");
        var second = registry.GetOrCreate(Key, "live");

        Assert.Same(first, second);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void GetOrCreate_ReplacesClosedSource()
    {
        var registry = new MediaSourceRegistry();
        var first = registry.GetOrCreate(Key, "live");
        first.Close();

        var second = registry.GetOrCreate(Key, "live");

        Assert.NotSame(first, second);
        Assert.False(second.IsClosed);
    }

    [Fact]
    public void Remove_ClosesSource()
    {
        var registry = new MediaSourceRegistry();
        var source = registry.GetOrCreate(Key, "live");

        Assert.True(registry.Remove(Key, out var removed));
        Assert.Same(source, removed);
        Assert.True(source.IsClosed);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task CleanupIdle_RemovesOnlyUnwatchedIdleSources()
    {
        var registry = new MediaSourceRegistry();
        var idle = registry.GetOrCreate(StreamKey.Create("vhost", "live", "idle"), "live");
        var watched = registry.GetOrCreate(StreamKey.Create("vhost", "live", "watched"), "live");
        using var sink = watched.CreateReader();

        await Task.Delay(20);
        var removed = registry.CleanupIdle(TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, removed);
        Assert.True(idle.IsClosed);
        Assert.False(watched.IsClosed);
        sink.Dispose();
    }
}
