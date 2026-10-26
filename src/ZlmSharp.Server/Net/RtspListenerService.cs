using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtsp;

namespace ZlmSharp.Server.Net;

internal sealed class RtspListenerService : TcpListenerHostedService
{
    private readonly RtspOptions _options;
    private readonly GeneralOptions _general;
    private readonly MediaSourceRegistry _registry;
    private readonly ILogger _sessionLogger;

    public RtspListenerService(AppOptions options, MediaSourceRegistry registry, ILoggerFactory loggerFactory)
        : base(loggerFactory.CreateLogger("ZlmSharp.Rtsp"))
    {
        _options = options.Rtsp;
        _general = options.General;
        _registry = registry;
        _sessionLogger = loggerFactory.CreateLogger("ZlmSharp.Rtsp.Session");
    }

    protected override string Protocol => "RTSP";

    protected override int Port => _options.Port;

    protected override async Task HandleAsync(TcpClient client, CancellationToken stoppingToken)
    {
        client.NoDelay = true;

        await using var stream = client.GetStream();
        var session = new RtspSession(
            stream,
            _registry,
            new RtspSessionOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.HandshakeSecond)),
                MaxStreamWaitMS = _general.MaxStreamWaitMS,
                WaitTrackReadyMS = _general.WaitTrackReadyMS,
            },
            Log,
            client.Client.RemoteEndPoint as IPEndPoint);

        await session.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    private void Log(RtspLogLevel level, string message)
    {
        var logLevel = level switch
        {
            RtspLogLevel.Debug => LogLevel.Debug,
            RtspLogLevel.Info => LogLevel.Information,
            RtspLogLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Error,
        };

        _sessionLogger.Log(logLevel, "{Message}", message);
    }
}
