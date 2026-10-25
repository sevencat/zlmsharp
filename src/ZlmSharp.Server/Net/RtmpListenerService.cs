using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;

namespace ZlmSharp.Server.Net;

internal sealed class RtmpListenerService : TcpListenerHostedService
{
    private readonly RtmpOptions _options;

    public RtmpListenerService(AppOptions options, ILoggerFactory loggerFactory)
        : base(loggerFactory.CreateLogger("ZlmSharp.Rtmp"))
    {
        _options = options.Rtmp;
    }

    protected override string Protocol => "RTMP";

    protected override int Port => _options.Port;
}
