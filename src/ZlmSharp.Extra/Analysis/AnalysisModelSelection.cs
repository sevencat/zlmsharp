using System.Collections.Concurrent;
using ZlmSharp.Core;

namespace ZlmSharp.Extra.Analysis;

/// <summary>
/// 按流指定推理模型：叠加流启动时写入（<c>startOverlay&amp;model=helmet.onnx</c>），
/// 分析调度采样该流时读取，实现"这一路流用哪个模型"。
/// 只影响被指定的流，不改变 <c>ZLM_YOLO_MODEL</c> 的全局默认值。
/// </summary>
public sealed class AnalysisModelSelection
{
    private readonly ConcurrentDictionary<StreamKey, string> _models = new();

    /// <summary>指定某路流使用的模型；传空表示恢复默认模型。</summary>
    public void Set(StreamKey key, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            _models.TryRemove(key, out _);
            return;
        }

        _models[key] = model.Trim();
    }

    /// <summary>取某路流指定的模型；未指定返回 null（用分析器默认模型）。</summary>
    public string? Get(StreamKey key) => _models.TryGetValue(key, out var model) ? model : null;

    /// <summary>清除某路流的指定模型（叠加停止时调用）。</summary>
    public void Remove(StreamKey key) => _models.TryRemove(key, out _);
}
