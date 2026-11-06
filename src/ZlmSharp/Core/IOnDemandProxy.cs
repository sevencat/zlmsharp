namespace ZlmSharp.Core;

/// <summary>一条按需拉流登记：本地流标识 + 源地址 + 当前是否正在拉。</summary>
public sealed record OnDemandProxyTarget(StreamKey Key, string Url, bool Pulling);

/// <summary>
/// 按需拉流：播放端发现目标流不在线时，触发预先登记好的拉流代理
/// （配置 <c>[proxy] on_demand_list</c>，或运行期用 <c>addStreamProxy?on_demand=1</c> / <see cref="Register"/> 添加）。
/// 协议层（RTMP / RTSP / HTTP-FLV）只依赖这个契约，不知道代理怎么实现。
/// </summary>
public interface IOnDemandProxy
{
    /// <summary>该流是否登记为按需拉流（不代表当前正在拉）。</summary>
    bool IsConfigured(StreamKey key);

    /// <summary>
    /// 确保该流开始拉流：已登记且当前没在拉时启动一次并返回 <c>true</c>；
    /// 未登记或已经在拉（包括播放端重复点播）返回 <c>false</c>。
    /// </summary>
    bool EnsureStarted(StreamKey key);

    /// <summary>
    /// 登记（或改写）一路按需拉流：登记后不会立刻拉流，等下一个播放请求触发。
    /// 返回 <c>true</c> 表示新增或地址已改变；地址没变返回 <c>false</c>。
    /// </summary>
    /// <exception cref="ArgumentException">app/stream 为空，或 url 既不是 rtmp:// 也不是 rtsp://。</exception>
    bool Register(StreamKey key, string url);

    /// <summary>移除一路按需拉流登记；正在拉流的会一并停止。未登记返回 <c>false</c>。</summary>
    bool Unregister(StreamKey key);

    /// <summary>列出所有按需拉流登记。</summary>
    IReadOnlyList<OnDemandProxyTarget> List();
}
