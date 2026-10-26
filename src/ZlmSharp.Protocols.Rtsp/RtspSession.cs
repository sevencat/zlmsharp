using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using ZlmSharp.Codecs;
using ZlmSharp.Core;

namespace ZlmSharp.Protocols.Rtsp;

public enum RtspLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>RTSP 会话日志回调（协议库不依赖 Microsoft.Extensions.Logging）。</summary>
public delegate void RtspLogHandler(RtspLogLevel level, string message);

public sealed class RtspSessionOptions
{
    /// <summary>空闲/握手超时：PLAY 之后不再生效。</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public int MaxStreamWaitMS { get; init; } = 5000;

    public int WaitTrackReadyMS { get; init; } = 2000;

    public string DefaultApp { get; init; } = "live";

    public int RtpPayloadType { get; init; } = 96;

    public int Mtu { get; init; } = 1400;

    public int SessionTimeoutSeconds { get; init; } = 60;

    /// <summary>RTCP 发送者报告间隔（TCP / UDP 都发）；<see cref="TimeSpan.Zero"/> 或负值表示不发。</summary>
    public TimeSpan RtcpInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// RTSP 服务端会话（一期为播放端）：
/// OPTIONS / DESCRIBE（SDP）/ SETUP（TCP interleaved 或 UDP）/ PLAY（RTP 打包 + RTCP SR）/ PAUSE / TEARDOWN。
/// </summary>
public sealed class RtspSession
{
    private const string PublicMethods =
        "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER, SET_PARAMETER";

    private readonly Stream _stream;
    private readonly MediaSourceRegistry _registry;
    private readonly RtspSessionOptions _options;
    private readonly RtspLogHandler? _log;
    private readonly RtspMessageReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly RtpPacketizer _rtp;
    private readonly string _sessionId;
    private readonly IPEndPoint? _remoteEndPoint;

    private MediaSource? _source;
    private StreamKey _key;
    private H264Config? _config;
    private string _baseUri = "";
    private int _rtpChannel;
    private int _rtcpChannel = 1;

    private MediaSink? _sink;
    private CancellationTokenSource? _playCts;
    private Task? _playTask;
    private long? _baseDts;

    private volatile UdpRtpTransport? _udp;

    // RTCP SR 统计：所有字段只在播放任务里读写
    private long _rtpAnchorTicks;
    private long _lastRtcpTicks;
    private uint _lastRtpTimestamp;
    private uint _rtpPacketCount;
    private uint _rtpOctetCount;

    /// <param name="remoteEndPoint">TCP 对端地址，UDP 传输时作为 RTP/RTCP 的目的 IP；为空则只能走 TCP。</param>
    public RtspSession(
        Stream stream,
        MediaSourceRegistry registry,
        RtspSessionOptions? options = null,
        RtspLogHandler? log = null,
        IPEndPoint? remoteEndPoint = null)
    {
        _stream = stream;
        _registry = registry;
        _options = options ?? new RtspSessionOptions();
        _log = log;
        _remoteEndPoint = remoteEndPoint;
        _reader = new RtspMessageReader(stream);
        _rtp = new RtpPacketizer((uint)RandomNumberGenerator.GetInt32(int.MaxValue), _options.RtpPayloadType, _options.Mtu);
        _sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    }

    public StreamKey? Key { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idleCts.CancelAfter(_options.HandshakeTimeout);

            while (!cancellationToken.IsCancellationRequested)
            {
                RtspRequest? request;
                try
                {
                    // 播放期间不再套用空闲超时（客户端只会周期性发 GET_PARAMETER）
                    var token = _playTask is null ? idleCts.Token : cancellationToken;
                    request = await _reader.ReadRequestAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Log(RtspLogLevel.Warning, "RTSP 会话空闲超时");
                    break;
                }

                if (request is null)
                {
                    break;
                }

                if (_playTask is null)
                {
                    idleCts.CancelAfter(_options.HandshakeTimeout);
                }

                await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidDataException exception)
        {
            Log(RtspLogLevel.Warning, $"RTSP 协议错误: {exception.Message}");
        }
        catch (IOException exception)
        {
            Log(RtspLogLevel.Debug, $"RTSP 连接中断: {exception.Message}");
        }
        finally
        {
            await StopPlaybackAsync().ConfigureAwait(false);
            await CloseUdpTransportAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleRequestAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method.ToUpperInvariant())
        {
            case "OPTIONS":
                await WriteResponseAsync(
                    request,
                    200,
                    "OK",
                    [("Public", PublicMethods)],
                    null,
                    cancellationToken).ConfigureAwait(false);
                break;

            case "DESCRIBE":
                await HandleDescribeAsync(request, cancellationToken).ConfigureAwait(false);
                break;

            case "SETUP":
                await HandleSetupAsync(request, cancellationToken).ConfigureAwait(false);
                break;

            case "PLAY":
                await HandlePlayAsync(request, cancellationToken).ConfigureAwait(false);
                break;

            case "PAUSE":
                await StopPlaybackAsync().ConfigureAwait(false);
                await WriteResponseAsync(
                    request,
                    200,
                    "OK",
                    [("Session", SessionHeader)],
                    null,
                    cancellationToken).ConfigureAwait(false);
                break;

            case "TEARDOWN":
                await StopPlaybackAsync().ConfigureAwait(false);
                await WriteResponseAsync(
                    request,
                    200,
                    "OK",
                    [("Session", SessionHeader)],
                    null,
                    cancellationToken).ConfigureAwait(false);
                break;

            case "GET_PARAMETER":
            case "SET_PARAMETER":
                await WriteResponseAsync(
                    request,
                    200,
                    "OK",
                    [("Session", SessionHeader)],
                    null,
                    cancellationToken).ConfigureAwait(false);
                break;

            default:
                await WriteResponseAsync(request, 405, "Method Not Allowed", null, null, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleDescribeAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        var source = _source ?? await ResolveSourceAsync(request.Uri, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            await WriteNotFoundAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        var config = _config ?? await H264ConfigResolver
            .WaitAsync(source, _options.WaitTrackReadyMS, cancellationToken)
            .ConfigureAwait(false);
        if (config is null)
        {
            Log(RtspLogLevel.Warning, $"RTSP DESCRIBE 失败：{_key} 缺少 SPS/PPS");
            await WriteNotFoundAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        _source = source;
        _config = config;
        _baseUri = BuildBaseUri(request.Uri);

        var sdp = SdpBuilder.BuildH264(config, _key.Stream, _options.RtpPayloadType);
        await WriteResponseAsync(
            request,
            200,
            "OK",
            [
                ("Content-Base", _baseUri + "/"),
                ("Content-Type", "application/sdp"),
            ],
            sdp,
            cancellationToken).ConfigureAwait(false);

        Log(RtspLogLevel.Info, $"RTSP DESCRIBE {_key} {source.Video.Width}x{source.Video.Height}");
    }

    private async Task HandleSetupAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        var transport = request.Header("Transport") ?? string.Empty;

        var responseTransport = await ConfigureTransportAsync(transport).ConfigureAwait(false);
        if (responseTransport is null)
        {
            Log(RtspLogLevel.Warning, $"RTSP SETUP 不支持的传输方式: {transport}");
            await WriteResponseAsync(request, 461, "Unsupported Transport", null, null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_source is null)
        {
            var source = await ResolveSourceAsync(request.Uri, cancellationToken).ConfigureAwait(false);
            if (source is not null)
            {
                _source = source;
                _baseUri = string.IsNullOrEmpty(_baseUri) ? BuildBaseUri(request.Uri) : _baseUri;
            }
        }

        await WriteResponseAsync(
            request,
            200,
            "OK",
            [
                ("Transport", responseTransport),
                ("Session", SessionHeader),
            ],
            null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 SETUP 的 Transport 头选择传输方式；不支持时返回 null（调用方回 461）。</summary>
    private async Task<string?> ConfigureTransportAsync(string transport)
    {
        var isTcp = transport.Contains("RTP/AVP/TCP", StringComparison.OrdinalIgnoreCase) ||
                    transport.Contains("interleaved=", StringComparison.OrdinalIgnoreCase);

        if (isTcp)
        {
            if (TryParseInterleaved(transport, out var rtpChannel, out var rtcpChannel))
            {
                _rtpChannel = rtpChannel;
                _rtcpChannel = rtcpChannel;
            }

            await CloseUdpTransportAsync().ConfigureAwait(false);
            return $"RTP/AVP/TCP;unicast;interleaved={_rtpChannel}-{_rtcpChannel};ssrc={_rtp.Ssrc:x8}";
        }

        if (!transport.Contains("RTP/AVP", StringComparison.OrdinalIgnoreCase) ||
            !TryParsePortPair(transport, "client_port=", out var clientRtpPort, out var clientRtcpPort))
        {
            return null;
        }

        if (_remoteEndPoint is null)
        {
            Log(RtspLogLevel.Warning, "RTSP SETUP 请求 UDP，但会话缺少对端地址");
            return null;
        }

        var udp = UdpRtpTransport.Create(_remoteEndPoint.Address, clientRtpPort, clientRtcpPort);
        await CloseUdpTransportAsync().ConfigureAwait(false);
        _udp = udp;
        Log(RtspLogLevel.Debug, $"RTSP UDP 传输 client_port={clientRtpPort}-{clientRtcpPort} server_port={udp.RtpPort}-{udp.RtcpPort}");

        return $"RTP/AVP;unicast;client_port={clientRtpPort}-{clientRtcpPort};" +
               $"server_port={udp.RtpPort}-{udp.RtcpPort};ssrc={_rtp.Ssrc:x8}";
    }

    private async Task CloseUdpTransportAsync()
    {
        var udp = _udp;
        _udp = null;
        if (udp is not null)
        {
            await udp.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandlePlayAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        var source = _source ?? await ResolveSourceAsync(request.Uri, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            await WriteNotFoundAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        var config = _config ?? await H264ConfigResolver
            .WaitAsync(source, _options.WaitTrackReadyMS, cancellationToken)
            .ConfigureAwait(false);
        if (config is null)
        {
            await WriteResponseAsync(request, 404, "Not Found", null, null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _source = source;
        _config = config;
        _baseUri = string.IsNullOrEmpty(_baseUri) ? BuildBaseUri(request.Uri) : _baseUri;

        var rtpInfo = $"{_baseUri}/trackID=0;seq={_rtp.SequenceNumber};rtptime=0";
        await WriteResponseAsync(
            request,
            200,
            "OK",
            [
                ("Session", SessionHeader),
                ("Range", "npt=0.000-"),
                ("RTP-Info", rtpInfo),
            ],
            null,
            cancellationToken).ConfigureAwait(false);

        // 先回 PLAY 响应再送 RTP，避免响应被 RTP 数据挤到后面
        if (_playTask is null)
        {
            StartPlayback(source, cancellationToken);
        }

        Log(RtspLogLevel.Info, $"RTSP PLAY {_key}");
    }

    private void StartPlayback(MediaSource source, CancellationToken cancellationToken)
    {
        var sink = source.CreateReader();
        _sink = sink;
        _baseDts = null;
        _lastRtcpTicks = 0;
        _rtpPacketCount = 0;
        _rtpOctetCount = 0;
        _lastRtpTimestamp = 0;
        _rtpAnchorTicks = Stopwatch.GetTimestamp();
        _playCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var playToken = _playCts.Token;
        _playTask = Task.Run(() => PlaybackLoopAsync(sink, playToken), playToken);
    }

    private async Task StopPlaybackAsync()
    {
        var cts = _playCts;
        var task = _playTask;
        var sink = _sink;
        var source = _source;

        _playCts = null;
        _playTask = null;
        _sink = null;

        if (cts is null && task is null && sink is null)
        {
            return;
        }

        cts?.Cancel();

        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log(RtspLogLevel.Warning, "RTSP 播放任务停止超时");
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }

        if (source is not null && sink is not null)
        {
            source.RemoveReader(sink);
        }

        sink?.Dispose();
        cts?.Dispose();
        Log(RtspLogLevel.Info, $"RTSP 结束播放 {_key}");
    }

    private async Task PlaybackLoopAsync(MediaSink sink, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in sink.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (frame.TrackType == TrackType.Video && !frame.IsConfig)
                    {
                        await SendVideoFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            sink.Dispose();
        }
    }

    private async Task SendVideoFrameAsync(MediaFrame frame, CancellationToken cancellationToken)
    {
        _baseDts ??= frame.Dts;
        var delta = Math.Max(0, frame.Dts - _baseDts.Value);
        var timestamp = (uint)(delta * 90); // 毫秒 → 90kHz

        foreach (var packet in _rtp.Packetize(frame.Data.Span, timestamp))
        {
            await SendRtpAsync(packet, cancellationToken).ConfigureAwait(false);
            _rtpPacketCount++;
            _rtpOctetCount += (uint)(packet.Length - RtpPacketizer.HeaderSize);
        }

        _lastRtpTimestamp = timestamp;
        _rtpAnchorTicks = Stopwatch.GetTimestamp();

        await SendSenderReportIfDueAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按间隔发送 RTCP SR（首帧后立即发一次，便于客户端尽快拿到同步源）。</summary>
    private async Task SendSenderReportIfDueAsync(CancellationToken cancellationToken)
    {
        var interval = _options.RtcpInterval;
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastRtcpTicks != 0 && Stopwatch.GetElapsedTime(_lastRtcpTicks, now) < interval)
        {
            return;
        }

        _lastRtcpTicks = now;

        // SR 里的 RTP 时间戳要对应 NTP 时刻，所以按首帧后的时钟做外推
        var rtpTimestamp = _lastRtpTimestamp + RtcpSenderReport.ToRtpDelta(Stopwatch.GetElapsedTime(_rtpAnchorTicks, now));
        var report = RtcpSenderReport.Build(
            _rtp.Ssrc,
            RtcpSenderReport.ToNtp(DateTimeOffset.UtcNow),
            rtpTimestamp,
            _rtpPacketCount,
            _rtpOctetCount);

        await SendRtcpAsync(report, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendRtpAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (_udp is { } udp)
        {
            await udp.SendRtpAsync(packet, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendInterleavedAsync(_rtpChannel, packet.Span, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendRtcpAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (_udp is { } udp)
        {
            await udp.SendRtcpAsync(packet, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendInterleavedAsync(_rtcpChannel, packet.Span, cancellationToken).ConfigureAwait(false);
    }

    private Task SendInterleavedAsync(int channel, ReadOnlySpan<byte> packet, CancellationToken cancellationToken)
    {
        var interleaved = new byte[4 + packet.Length];
        interleaved[0] = (byte)'$';
        interleaved[1] = (byte)channel;
        BinaryPrimitives.WriteUInt16BigEndian(interleaved.AsSpan(2), (ushort)packet.Length);
        packet.CopyTo(interleaved.AsSpan(4));

        return WriteAsync(interleaved, cancellationToken);
    }

    private async Task<MediaSource?> ResolveSourceAsync(string uri, CancellationToken cancellationToken)
    {
        if (!TryParseTarget(uri, out var app, out var stream, out var vhost))
        {
            Log(RtspLogLevel.Warning, $"RTSP 地址非法: {uri}");
            return null;
        }

        var key = StreamKey.Create(vhost, app.Length > 0 ? app : _options.DefaultApp, stream);
        var source = await WaitForSourceAsync(key, _options.MaxStreamWaitMS, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            Log(RtspLogLevel.Warning, $"RTSP 流不存在: {key}");
            return null;
        }

        _key = key;
        Key = key;
        return source;
    }

    private async Task<MediaSource?> WaitForSourceAsync(
        StreamKey key,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);

        while (true)
        {
            if (_registry.TryGet(key, out var source) && source is { IsClosed: false })
            {
                return source;
            }

            if (timeoutMs <= 0 || Environment.TickCount64 >= deadline)
            {
                return null;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task WriteNotFoundAsync(RtspRequest request, CancellationToken cancellationToken) =>
        WriteResponseAsync(request, 404, "Not Found", null, null, cancellationToken);

    private Task WriteResponseAsync(
        RtspRequest request,
        int statusCode,
        string reason,
        IReadOnlyList<(string Name, string Value)>? headers,
        string? body,
        CancellationToken cancellationToken)
    {
        var bytes = RtspResponse.Build(statusCode, reason, request.CSeq, headers, body);
        return WriteAsync(bytes, cancellationToken);
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string SessionHeader => $"{_sessionId};timeout={_options.SessionTimeoutSeconds}";

    private static bool TryParseInterleaved(string transport, out int rtpChannel, out int rtcpChannel)
    {
        if (TryParsePortPair(transport, "interleaved=", out rtpChannel, out rtcpChannel))
        {
            return true;
        }

        rtpChannel = 0;
        rtcpChannel = 1;
        return false;
    }

    /// <summary>解析 <c>interleaved=0-1</c> / <c>client_port=8000-8001</c> 这类端口对。</summary>
    private static bool TryParsePortPair(string transport, string name, out int first, out int second)
    {
        first = 0;
        second = 0;

        var index = transport.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var start = index + name.Length;
        var end = transport.IndexOfAny([';', ' ', '\r', '\n'], start);
        var value = (end < 0 ? transport[start..] : transport[start..end]).Trim().Trim('"');
        var parts = value.Split('-');
        if (parts.Length == 0 || !int.TryParse(parts[0], out first) || first is < 0 or > 65535)
        {
            return false;
        }

        second = parts.Length > 1 && int.TryParse(parts[1], out var parsed) && parsed is >= 0 and <= 65535
            ? parsed
            : first + 1;

        return second <= 65535;
    }

    /// <summary>解析 <c>rtsp://host:port/[vhost/]app/stream[/trackID=0]</c>。</summary>
    private static bool TryParseTarget(string uri, out string app, out string stream, out string? vhost)
    {
        app = string.Empty;
        stream = string.Empty;
        vhost = null;

        var path = uri;
        if (uri.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return false;
            }

            path = parsed.AbsolutePath;
        }

        var queryIndex = path.IndexOfAny(['?', '#']);
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && segments[^1].StartsWith("trackID", StringComparison.OrdinalIgnoreCase))
        {
            segments = segments[..^1];
        }

        if (segments.Length == 2)
        {
            app = segments[0];
            stream = segments[1];
        }
        else if (segments.Length == 3)
        {
            vhost = segments[0];
            app = segments[1];
            stream = segments[2];
        }
        else
        {
            return false;
        }

        return app.Length > 0 && stream.Length > 0;
    }

    private static string BuildBaseUri(string uri)
    {
        var trimmed = uri.TrimEnd('/');
        var trackIndex = trimmed.LastIndexOf("/trackID", StringComparison.OrdinalIgnoreCase);
        return trackIndex > 0 ? trimmed[..trackIndex] : trimmed;
    }

    private void Log(RtspLogLevel level, string message) => _log?.Invoke(level, message);
}
