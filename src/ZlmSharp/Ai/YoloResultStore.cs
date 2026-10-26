using System.Collections.Concurrent;

namespace ZlmSharp.Ai;

/// <summary>某路流最近一次的检测结果（定时检测写入，API 读取）。</summary>
public sealed record YoloSnapshot(
    string Vhost,
    string App,
    string Stream,
    DateTimeOffset At,
    long ElapsedMs,
    int Width,
    int Height,
    IReadOnlyList<YoloObject> Objects,
    string? Error);

public sealed class YoloResultStore
{
    private readonly ConcurrentDictionary<string, YoloSnapshot> _snapshots = new(StringComparer.Ordinal);

    public void Set(YoloSnapshot snapshot) => _snapshots[Id(snapshot.Vhost, snapshot.App, snapshot.Stream)] = snapshot;

    public YoloSnapshot? Get(string vhost, string app, string stream) =>
        _snapshots.TryGetValue(Id(vhost, app, stream), out var snapshot) ? snapshot : null;

    public IReadOnlyList<YoloSnapshot> List(string vhost, string app, string stream) =>
        _snapshots.Values
            .Where(item => Matches(item.Vhost, vhost) && Matches(item.App, app) && Matches(item.Stream, stream))
            .OrderByDescending(item => item.At)
            .ToArray();

    public void Remove(string vhost, string app, string stream) => _snapshots.TryRemove(Id(vhost, app, stream), out _);

    private static string Id(string vhost, string app, string stream) => $"{vhost}/{app}/{stream}";

    private static bool Matches(string value, string filter) =>
        filter.Length == 0 || string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);
}
