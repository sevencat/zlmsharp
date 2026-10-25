using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;

namespace ZlmSharp.Server.Net;

internal sealed class RtspListenerService : TcpListenerHostedService
{
    private readonly RtspOptions _options;

    public RtspListenerService(AppOptions options, ILoggerFactory loggerFactory)
        : base(loggerFactory.CreateLogger("ZlmSharp.Rtsp"))
    {
        _options = options.Rtsp;
    }

    protected override string Protocol => "RTSP";

    protected override int Port => _options.Port;
}
