using ZlmSharp.Ai.Analysis;

namespace ZlmSharp.Extra.Analysis;

/// <summary>已注册的结果旁路集合；声明 <see cref="AnalysisOutputs.Sink"/> 的分析器结果会推给这里的所有实现。</summary>
public sealed class AnalysisSinks
{
    private readonly List<IAnalysisSink> _sinks = [];

    public IReadOnlyList<IAnalysisSink> All
    {
        get
        {
            lock (_sinks)
            {
                return _sinks.ToArray();
            }
        }
    }

    public void Add(IAnalysisSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_sinks)
        {
            _sinks.Add(sink);
        }
    }
}
