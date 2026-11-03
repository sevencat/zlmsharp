using System.Collections.Concurrent;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Core;

namespace ZlmSharp.Extra.Analysis;

/// <summary>
/// 叠加用的最新结果缓存：按（流, 分析器）保存最近一次结果，叠加管线按需取用。
/// 与 <see cref="AnalysisResultStore"/> 的分工：那个面向 API 查询，这个只服务叠加渲染。
/// </summary>
public sealed class OverlayResultCache
{
    private readonly ConcurrentDictionary<string, AnalysisResult> _results = new(StringComparer.Ordinal);

    public void Set(AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _results[Id(result.Key, result.AnalyzerId)] = result;
    }

    /// <summary>某路流全部分析器的最新结果（时间倒序）。</summary>
    public IReadOnlyList<AnalysisResult> List(StreamKey key)
    {
        var prefix = Prefix(key);
        return _results
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .OrderByDescending(item => item.At)
            .ToArray();
    }

    public AnalysisResult? Get(StreamKey key, string analyzerId) =>
        _results.TryGetValue(Id(key, analyzerId), out var result) ? result : null;

    public void Remove(StreamKey key, string analyzerId) => _results.TryRemove(Id(key, analyzerId), out _);

    public void RemoveStream(StreamKey key)
    {
        foreach (var pair in _results)
        {
            if (pair.Value.Key == key)
            {
                _results.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string Prefix(StreamKey key) => $"{key.Vhost}/{key.App}/{key.Stream}#";

    private static string Id(StreamKey key, string analyzerId) => $"{key.Vhost}/{key.App}/{key.Stream}#{analyzerId}";
}
