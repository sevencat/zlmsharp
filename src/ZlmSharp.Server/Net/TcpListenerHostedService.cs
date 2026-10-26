using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZlmSharp.Server.Net;

/// <summary>
/// RTSP/RTMP 裸 TCP 监听基类。M0 只做端口占用与连接接入验证，协议处理在 M5/M6 落地。
/// </summary>
internal abstract class TcpListenerHostedService : BackgroundService
{
    private readonly ILogger _logger;

    protected TcpListenerHostedService(ILogger logger)
    {
        _logger = logger;
    }

    protected abstract string Protocol { get; }

    protected abstract int Port { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = Port;
        if (port <= 0)
        {
            _logger.LogInformation("{Protocol} 监听未启用（端口为 0）", Protocol);
            return;
        }

        var listener = StartListener(port);
        if (listener is null)
        {
            return;
        }

        _logger.LogInformation("{Protocol} 监听 0.0.0.0:{Port}", Protocol, port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = HandleSafelyAsync(client, stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    protected virtual Task HandleAsync(TcpClient client, CancellationToken stoppingToken)
    {
        _logger.LogWarning("{Protocol} 收到连接 {Endpoint}，但协议实现尚未完成，关闭连接", Protocol, client.Client.RemoteEndPoint);
        return Task.CompletedTask;
    }

    private TcpListener? StartListener(int port)
    {
        try
        {
            var dual = new TcpListener(IPAddress.IPv6Any, port);
            dual.Server.DualMode = true;
            dual.Start();
            return dual;
        }
        catch (SocketException)
        {
            // IPv6 不可用时退回 IPv4
        }

        try
        {
            var v4 = new TcpListener(IPAddress.Any, port);
            v4.Start();
            return v4;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Protocol} 端口 {Port} 监听失败，该协议不可用", Protocol, port);
            return null;
        }
    }

    private async Task HandleSafelyAsync(TcpClient client, CancellationToken stoppingToken)
    {
        try
        {
            await HandleAsync(client, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            _logger.LogDebug(ex, "{Protocol} 会话读写异常", Protocol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Protocol} 会话异常", Protocol);
        }
        finally
        {
            client.Dispose();
        }
    }
}
