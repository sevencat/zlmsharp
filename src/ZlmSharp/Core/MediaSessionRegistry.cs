using System.Collections.Concurrent;

namespace ZlmSharp.Core;

public enum MediaSessionType
{
    /// <summary>推流端。</summary>
    Publisher,

    /// <summary>播放端（含 HTTP-FLV / WS-FLV）。</summary>
    Player,
}

/// <summary>一个在线媒体会话（推流或播放）。</summary>
public sealed record MediaSessionInfo(
    string Id,
    string Schema,
    MediaSessionType Type,
    StreamKey Key,
    string? Ip,
    int Port,
    string? UserAgent,
    DateTime CreatedAt);

/// <summary>
/// 在线会话注册表：<c>getSessionList</c> 读取，<c>kick_session</c> 通过保存的取消回调强制断开。
/// 会话退出时会自行 <see cref="Unregister"/>，因此 Kick 先摘除条目再触发取消。
/// </summary>
public sealed class MediaSessionRegistry
{
    private sealed record Entry(MediaSessionInfo Info, Action Kick);

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    /// <summary>登记一个会话；<paramref name="kick"/> 在 <c>kick_session</c> 时被调用（通常取消该连接的 token）。</summary>
    public MediaSessionInfo Register(
        string schema,
        MediaSessionType type,
        StreamKey key,
        string? ip,
        int port,
        string? userAgent,
        Action kick)
    {
        ArgumentNullException.ThrowIfNull(kick);

        var typeName = type == MediaSessionType.Publisher ? "publisher" : "player";
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var id = $"{schema}-{typeName}-{suffix}";

        var info = new MediaSessionInfo(id, schema, type, key, ip, port, userAgent, DateTime.UtcNow);
        _sessions[id] = new Entry(info, kick);
        return info;
    }

    public bool Unregister(string? id) =>
        !string.IsNullOrEmpty(id) && _sessions.TryRemove(id, out _);

    public IReadOnlyList<MediaSessionInfo> Snapshot(Func<MediaSessionInfo, bool>? predicate = null)
    {
        var result = new List<MediaSessionInfo>(_sessions.Count);
        foreach (var entry in _sessions.Values)
        {
            if (predicate is null || predicate(entry.Info))
            {
                result.Add(entry.Info);
            }
        }

        result.Sort(static (left, right) => left.CreatedAt.CompareTo(right.CreatedAt));
        return result;
    }

    /// <summary>按条件踢人，返回被踢的会话信息。</summary>
    public IReadOnlyList<MediaSessionInfo> Kick(Func<MediaSessionInfo, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var kicked = new List<MediaSessionInfo>();
        foreach (var pair in _sessions)
        {
            if (!predicate(pair.Value.Info))
            {
                continue;
            }

            if (!_sessions.TryRemove(pair))
            {
                continue;
            }

            kicked.Add(pair.Value.Info);
            try
            {
                pair.Value.Kick();
            }
            catch (Exception)
            {
                // 取消回调本身不应抛异常；真抛了也不影响其它会话
            }
        }

        return kicked;
    }
}
