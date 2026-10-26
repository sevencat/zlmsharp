using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Rtsp;

namespace ZlmSharp.Server.Net;

internal sealed class RtspListenerService : TcpListenerHostedService
{
    private const string PublicMethods =
        "OPTIONS, DESCRIBE, ANNOUNCE, SETUP, PLAY, PAUSE, RECORD, TEARDOWN, GET_PARAMETER, SET_PARAMETER";

    private readonly RtspOptions _options;
    private readonly GeneralOptions _general;
    private readonly MediaSourceRegistry _registry;
    private readonly ILogger _sessionLogger;
    private readonly MediaSessionRegistry? _sessions;
    private readonly MediaHookHandler? _hook;

    public RtspListenerService(
        AppOptions options,
        MediaSourceRegistry registry,
        ILoggerFactory loggerFactory,
        MediaSessionRegistry? sessions = null,
        MediaHookHandler? hook = null)
        : base(loggerFactory.CreateLogger("ZlmSharp.Rtsp"))
    {
        _options = options.Rtsp;
        _general = options.General;
        _registry = registry;
        _sessions = sessions;
        _hook = hook;
        _sessionLogger = loggerFactory.CreateLogger("ZlmSharp.Rtsp.Session");
    }

    protected override string Protocol => "RTSP";

    protected override int Port => _options.Port;

    protected override async Task HandleAsync(TcpClient client, CancellationToken stoppingToken)
    {
        client.NoDelay = true;

        await using var stream = client.GetStream();
        var reader = new RtspMessageReader(stream);
        var options = new RtspSessionOptions
        {
            HandshakeTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.HandshakeSecond)),
            MaxStreamWaitMS = _general.MaxStreamWaitMS,
            WaitTrackReadyMS = _general.WaitTrackReadyMS,
        };
        var remote = client.Client.RemoteEndPoint as IPEndPoint;

        // OPTIONS 推/拉都会先发：必须先回它，客户端才会继续发 ANNOUNCE/DESCRIBE
        var pending = new List<RtspRequest>(2);
        var isPush = false;
        using (var firstCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            firstCts.CancelAfter(options.HandshakeTimeout);
            try
            {
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var request = await reader.ReadRequestAsync(firstCts.Token).ConfigureAwait(false);
                    if (request is null)
                    {
                        return;
                    }

                    if (request.MethodIs("OPTIONS"))
                    {
                        var bytes = RtspResponse.Build(
                            200,
                            "OK",
                            request.CSeq,
                            [("Public", PublicMethods)],
                            null);
                        await stream.WriteAsync(bytes, firstCts.Token).ConfigureAwait(false);
                        await stream.FlushAsync(firstCts.Token).ConfigureAwait(false);
                        continue;
                    }

                    pending.Add(request);
                    isPush = request.MethodIs("ANNOUNCE");
                    break;
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                Log(RtspLogLevel.Warning, "RTSP 等待首条请求超时");
                return;
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        var userAgent = pending[0].Header("User-Agent");

        if (isPush)
        {
            // 推流：ANNOUNCE → SETUP → RECORD
            var push = new RtspPushSession(
                stream,
                reader,
                pending,
                _registry,
                options,
                Log,
                remote,
                _hook,
                _sessions,
                userAgent);
            await push.RunAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        var session = new RtspSession(
            stream,
            _registry,
            options,
            Log,
            remote,
            reader,
            pending,
            _hook,
            _sessions,
            userAgent);
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
