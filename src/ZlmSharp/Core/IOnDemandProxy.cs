namespace ZlmSharp.Core;

/// <summary>
/// 按需拉流：播放端发现目标流不在线时，触发预先配置好的拉流代理
/// （<c>[proxy] on_demand_list</c> 里的 <c>app/stream=url</c>）。
/// 协议层（RTMP / RTSP / HTTP-FLV）只依赖这个契约，不知道代理怎么实现。
/// </summary>
public interface IOnDemandProxy
{
    /// <summary>该流是否配置为按需拉流（不代表当前正在拉）。</summary>
    bool IsConfigured(StreamKey key);

    /// <summary>
    /// 确保该流开始拉流：已配置且当前没在拉时启动一次并返回 <c>true</c>；
    /// 未配置或已经在拉（包括播放端重复点播）返回 <c>false</c>。
    /// </summary>
    bool EnsureStarted(StreamKey key);
}
