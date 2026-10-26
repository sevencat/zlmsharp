using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Server.Net;

/// <summary>
/// <c>[proxy] enable=1</c> 时按 <c>list</c> 启动即拉：
/// 每项 <c>app/stream=rtmp://...</c> 或 <c>vhost/app/stream=rtsp://...</c>，分号分隔。
/// 拉流失败由 <see cref="RtmpPullProxyManager"/> 自己重连，这里只负责起一次。
/// </summary>
internal sealed class ProxyAutoStartService : IHostedService
{
    private readonly AppOptions _options;
    private readonly RtmpPullProxyManager _manager;
    private readonly ILogger _logger;

    public ProxyAutoStartService(
        AppOptions options,
        RtmpPullProxyManager manager,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _manager = manager;
        _logger = loggerFactory.CreateLogger("ZlmSharp.Proxy");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var proxy = _options.Proxy;
        if (!proxy.Enable)
        {
            return Task.CompletedTask;
        }

        var items = RtmpPullProxyManager.ParseList(proxy.List);
        if (items.Count == 0)
        {
            _logger.LogWarning("proxy.enable=1 但 list 为空，没有启动任何拉流代理");
            return Task.CompletedTask;
        }

        var started = 0;
        foreach (var (key, url) in items)
        {
            try
            {
                _manager.Start(key.Vhost, key.App, key.Stream, url);
                started++;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "启动拉流代理失败 {Key} ← {Url}", key, url);
            }
        }

        _logger.LogInformation("拉流代理已启动 {Started}/{Total} 路", started, items.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
