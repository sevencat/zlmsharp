using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Server.Net;

internal sealed class RtmpListenerService : TcpListenerHostedService
{
    private readonly RtmpOptions _options;
    private readonly GeneralOptions _general;
    private readonly MediaSourceRegistry _registry;
    private readonly ILogger _sessionLogger;
    private readonly MediaSessionRegistry? _sessions;
    private readonly MediaHookHandler? _hook;

    public RtmpListenerService(
        AppOptions options,
        MediaSourceRegistry registry,
        ILoggerFactory loggerFactory,
        MediaSessionRegistry? sessions = null,
        MediaHookHandler? hook = null)
        : base(loggerFactory.CreateLogger("ZlmSharp.Rtmp"))
    {
        _options = options.Rtmp;
        _general = options.General;
        _registry = registry;
        _sessions = sessions;
        _hook = hook;
        _sessionLogger = loggerFactory.CreateLogger("ZlmSharp.Rtmp.Session");
    }

    protected override string Protocol => "RTMP";

    protected override int Port => _options.Port;

    protected override async Task HandleAsync(TcpClient client, CancellationToken stoppingToken)
    {
        client.NoDelay = true;

        await using var stream = client.GetStream();
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        var session = new RtmpSession(
            stream,
            _registry,
            new RtmpSessionOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.HandshakeSecond)),
                MaxStreamWaitMS = _general.MaxStreamWaitMS,
                WaitTrackReadyMS = _general.WaitTrackReadyMS,
            },
            Log,
            _hook,
            _sessions,
            remote?.Address.ToString(),
            remote?.Port ?? 0);

        await session.RunAsync(stoppingToken).ConfigureAwait(false);
    }

    private void Log(RtmpLogLevel level, string message)
    {
        var logLevel = level switch
        {
            RtmpLogLevel.Debug => LogLevel.Debug,
            RtmpLogLevel.Info => LogLevel.Information,
            RtmpLogLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Error,
        };

        _sessionLogger.Log(logLevel, "{Message}", message);
    }
}
