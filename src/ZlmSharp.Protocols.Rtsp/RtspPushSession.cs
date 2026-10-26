using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ZlmSharp.Codecs;
using ZlmSharp.Core;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>
/// RTSP 推流会话（ANNOUNCE → SETUP → RECORD）：解析 SDP 里的 H.264 参数集，
/// 接收 RTP（TCP interleaved 或 UDP）并解包成 Annex-B 写入 <see cref="MediaSource"/>。
/// 只支持 H.264 视频（音频推流后续再加），B 帧按无重排处理（时间戳直接当 DTS）。
/// </summary>
public sealed class RtspPushSession
{
    private const string PublicMethods =
        "OPTIONS, ANNOUNCE, SETUP, RECORD, TEARDOWN, GET_PARAMETER, SET_PARAMETER";

    private readonly Stream _stream;
    private readonly RtspMessageReader _reader;
    private readonly IReadOnlyList<RtspRequest> _pendingRequests;
    private readonly MediaSourceRegistry _registry;
    private readonly RtspSessionOptions _options;
    private readonly RtspLogHandler? _log;
    private readonly IPEndPoint? _remoteEndPoint;
    private readonly MediaHookHandler? _hook;
    private readonly MediaSessionRegistry? _sessions;
    private readonly string? _userAgent;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly H264RtpDepacketizer _depacketizer = new();
    private readonly string _sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    private MediaSource? _source;
    private StreamKey _key;
    private string _baseUri = string.Empty;
    private int _payloadType = 96;
    private int _rtpChannel = 0;
    private int _rtcpChannel = 1;
    private bool _recording;
    private CancellationTokenSource? _lifetimeCts;
    private MediaSessionInfo? _publishSession;

    private UdpClient? _udpRtp;
    private UdpClient? _udpRtcp;
    private CancellationTokenSource? _udpCts;
    private Task? _udpRtpTask;
    private Task? _udpRtcpTask;

    private ushort? _lastSequence;
    private uint? _baseTimestamp;
    private long _lastDts;

    public RtspPushSession(
        Stream stream,
        RtspMessageReader reader,
        IReadOnlyList<RtspRequest> pendingRequests,
        MediaSourceRegistry registry,
        RtspSessionOptions? options = null,
        RtspLogHandler? log = null,
        IPEndPoint? remoteEndPoint = null,
        MediaHookHandler? hook = null,
        MediaSessionRegistry? sessions = null,
        string? userAgent = null)
    {
        _stream = stream;
        _reader = reader;
        _pendingRequests = pendingRequests;
        _registry = registry;
        _options = options ?? new RtspSessionOptions();
        _log = log;
        _remoteEndPoint = remoteEndPoint;
        _hook = hook;
        _sessions = sessions;
        _userAgent = userAgent;
    }

    public StreamKey? Key => _source is null ? null : _key;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // 会话级 token：kick_session 通过取消它来强制断开连接
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lifetimeCts = lifetime;
        var token = lifetime.Token;

        try
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            idleCts.CancelAfter(_options.HandshakeTimeout);

            var announced = false;
            foreach (var pending in _pendingRequests)
            {
                if (pending.MethodIs("ANNOUNCE"))
                {
                    if (!await HandleAnnounceAsync(pending, token).ConfigureAwait(false))
                    {
                        return;
                    }

                    announced = true;
                }
                else if (!await HandleRequestAsync(pending, token).ConfigureAwait(false))
                {
                    return;
                }
            }

            if (!announced)
            {
                Log(RtspLogLevel.Warning, "RTSP 推流缺少 ANNOUNCE");
                return;
            }

            while (!token.IsCancellationRequested)
            {
                RtspReadItem item;
                try
                {
                    // RECORD 之后客户端只发 RTP（和偶发 GET_PARAMETER），不能再套握手超时
                    item = await _reader.ReadNextAsync(_recording ? token : idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Log(RtspLogLevel.Warning, "RTSP 推流会话空闲超时");
                    break;
                }

                if (item.Request is null && item.Payload is null)
                {
                    break;
                }

                if (!_recording)
                {
                    idleCts.CancelAfter(_options.HandshakeTimeout);
                }

                if (item.Request is { } request)
                {
                    if (!await HandleRequestAsync(request, token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                else if (_recording && item.Channel == _rtpChannel && item.Payload is { Length: > 0 } payload)
                {
                    ProcessRtp(payload);
                }
            }
        }
        catch (InvalidDataException exception)
        {
            Log(RtspLogLevel.Warning, $"RTSP 推流协议错误: {exception.Message}");
        }
        catch (IOException exception)
        {
            Log(RtspLogLevel.Debug, $"RTSP 推流连接中断: {exception.Message}");
        }
        finally
        {
            _lifetimeCts = null;
            await StopUdpAsync().ConfigureAwait(false);
            CloseSource();
        }
    }

    private async Task<bool> HandleAnnounceAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        if (!RtspUri.TryParse(request.Uri, out var app, out var stream, out var vhost))
        {
            await WriteErrorAsync(request, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var sdp = Encoding.UTF8.GetString(request.Body);
        if (!SdpParser.TryParseH264(sdp, out var track, out var error) || track is null)
        {
            Log(RtspLogLevel.Warning, $"RTSP ANNOUNCE SDP 非法: {error}");
            await WriteErrorAsync(request, 400, "Bad Request", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var key = StreamKey.Create(vhost, app, stream);
        if (_registry.TryGet(key, out var existing) && existing is { IsClosed: false } && existing.Video.Sps is not null)
        {
            Log(RtspLogLevel.Warning, $"RTSP 推流被拒绝：{key} 已有发布者");
            await WriteErrorAsync(request, 453, "Not Enough Bandwidth", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var session = RegisterSession(key);
        if (!await AuthorizeAsync(key, session, request, cancellationToken).ConfigureAwait(false))
        {
            _sessions?.Unregister(session?.Id);
            await WriteErrorAsync(request, 403, "Forbidden", cancellationToken).ConfigureAwait(false);
            return false;
        }

        _publishSession = session;

        var source = _registry.GetOrCreate(key, "rtsp");
        _source = source;
        _key = key;
        _baseUri = RtspUri.BuildBaseUri(request.Uri);
        _payloadType = track.PayloadType;

        source.Video.SetCodec(CodecId.H264);
        if (track.HasParameterSets)
        {
            ApplyParameterSets(track.Sps!, track.Pps!, 0, configFrameKeyFlag: true);
            Log(RtspLogLevel.Info, $"RTSP 收到推流配置 {key} {source.Video.Width}x{source.Video.Height}");
        }
        else
        {
            Log(RtspLogLevel.Info, $"RTSP 开始推流 {key}（等待带内 SPS/PPS）");
        }

        await WriteResponseAsync(
            request,
            200,
            "OK",
            [("Session", SessionHeader), ("Content-Base", _baseUri + "/")],
            null,
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task<bool> HandleRequestAsync(RtspRequest request, CancellationToken cancellationToken)
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
                return true;

            case "SETUP":
                await HandleSetupAsync(request, cancellationToken).ConfigureAwait(false);
                return true;

            case "RECORD":
                _recording = true;
                Log(RtspLogLevel.Info, $"RTSP 开始推流数据 {_key}");
                await WriteResponseAsync(
                    request,
                    200,
                    "OK",
                    [("Session", SessionHeader)],
                    null,
                    cancellationToken).ConfigureAwait(false);
                return true;

            case "TEARDOWN":
                await WriteResponseAsync(
                    request,
                    200,
                    "OK",
                    [("Session", SessionHeader)],
                    null,
                    cancellationToken).ConfigureAwait(false);
                return false;

            case "GET_PARAMETER":
            case "SET_PARAMETER":
                await WriteResponseAsync(
                    request,
                    200,
                    "OK",
                    [("Session", SessionHeader)],
                    null,
                    cancellationToken).ConfigureAwait(false);
                return true;

            default:
                await WriteResponseAsync(request, 405, "Method Not Allowed", null, null, cancellationToken)
                    .ConfigureAwait(false);
                return true;
        }
    }

    private async Task HandleSetupAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        var transport = request.Header("Transport") ?? string.Empty;
        var isTcp = transport.Contains("RTP/AVP/TCP", StringComparison.OrdinalIgnoreCase) ||
                    transport.Contains("interleaved=", StringComparison.OrdinalIgnoreCase);

        if (isTcp)
        {
            if (TryParsePortPair(transport, "interleaved=", out var rtpChannel, out var rtcpChannel))
            {
                _rtpChannel = rtpChannel;
                _rtcpChannel = rtcpChannel;
            }

            await StopUdpAsync().ConfigureAwait(false);
            await WriteResponseAsync(
                request,
                200,
                "OK",
                [
                    ("Transport", $"RTP/AVP/TCP;unicast;interleaved={_rtpChannel}-{_rtcpChannel};ssrc={Ssrc:x8}"),
                    ("Session", SessionHeader),
                ],
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!transport.Contains("RTP/AVP", StringComparison.OrdinalIgnoreCase) ||
            !TryParsePortPair(transport, "client_port=", out var clientRtpPort, out var clientRtcpPort))
        {
            Log(RtspLogLevel.Warning, $"RTSP 推流 SETUP 不支持的传输方式: {transport}");
            await WriteResponseAsync(request, 461, "Unsupported Transport", null, null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!TryStartUdp(cancellationToken, out var serverRtpPort, out var serverRtcpPort))
        {
            await WriteResponseAsync(request, 461, "Unsupported Transport", null, null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        Log(
            RtspLogLevel.Debug,
            $"RTSP 推流 UDP client_port={clientRtpPort}-{clientRtcpPort} server_port={serverRtpPort}-{serverRtcpPort}");

        await WriteResponseAsync(
            request,
            200,
            "OK",
            [
                ("Transport",
                    $"RTP/AVP;unicast;client_port={clientRtpPort}-{clientRtcpPort};" +
                    $"server_port={serverRtpPort}-{serverRtcpPort};ssrc={Ssrc:x8}"),
                ("Session", SessionHeader),
            ],
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private uint Ssrc { get; } = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);

    private bool TryStartUdp(CancellationToken cancellationToken, out int rtpPort, out int rtcpPort)
    {
        rtpPort = 0;
        rtcpPort = 0;

        UdpClient? rtp = null;
        UdpClient? rtcp = null;
        try
        {
            rtp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            rtcp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            rtpPort = ((IPEndPoint)rtp.Client.LocalEndPoint!).Port;
            rtcpPort = ((IPEndPoint)rtcp.Client.LocalEndPoint!).Port;
        }
        catch (SocketException exception)
        {
            Log(RtspLogLevel.Warning, $"RTSP 推流 UDP 端口分配失败: {exception.Message}");
            rtp?.Dispose();
            rtcp?.Dispose();
            return false;
        }

        _udpRtp = rtp;
        _udpRtcp = rtcp;
        _udpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _udpCts.Token;
        _udpRtpTask = Task.Run(() => UdpRtpLoopAsync(rtp, token), CancellationToken.None);
        _udpRtcpTask = Task.Run(() => UdpRtcpLoopAsync(rtcp, token), CancellationToken.None);
        return true;
    }

    private async Task UdpRtpLoopAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            if (_remoteEndPoint is { } remote &&
                !result.RemoteEndPoint.Address.Equals(remote.Address))
            {
                continue;
            }

            if (_recording)
            {
                ProcessRtp(result.Buffer);
            }
        }
    }

    private async Task UdpRtcpLoopAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
            }
        }
    }

    private void ProcessRtp(byte[] packet)
    {
        if (_source is null || !RtpPacketHeader.TryParse(packet, out var header))
        {
            return;
        }

        if (header.PayloadType != _payloadType)
        {
            return;
        }

        if (_lastSequence is { } last)
        {
            var diff = (ushort)(header.SequenceNumber - last);
            if (diff is 0 or > 0x8000)
            {
                return;
            }
        }

        _lastSequence = header.SequenceNumber;

        if (!_depacketizer.Push(
                packet.AsSpan(header.PayloadOffset, header.PayloadLength),
                header.Marker,
                out var accessUnit,
                out var keyFrame) ||
            accessUnit is null)
        {
            return;
        }

        var dts = ToDts(header.Timestamp);
        EnsureInBandConfig(accessUnit, dts);

        _source.AddFrame(MediaFrame.Create(
            accessUnit,
            TrackType.Video,
            CodecId.H264,
            dts,
            dts,
            keyFrame ? FrameFlags.Key : FrameFlags.None));
    }

    private long ToDts(uint timestamp)
    {
        if (_baseTimestamp is null)
        {
            _baseTimestamp = timestamp;
            _lastDts = 0;
            return 0;
        }

        var delta = unchecked(timestamp - _baseTimestamp.Value);
        if (delta > 0x80000000)
        {
            // 时间戳回退（B 帧/乱序）：按无重排处理，保持单调
            return _lastDts;
        }

        var dts = (long)delta * 1000 / 90_000;
        if (dts < _lastDts)
        {
            dts = _lastDts;
        }

        _lastDts = dts;
        return dts;
    }

    private void EnsureInBandConfig(byte[] accessUnit, long dts)
    {
        if (_source is null || _source.Video.Sps is not null)
        {
            return;
        }

        byte[]? sps = null;
        byte[]? pps = null;
        foreach (var nal in AnnexB.Split(accessUnit))
        {
            switch (H264NalUnit.GetType(nal))
            {
                case H264NalUnitType.Sps:
                    sps ??= nal;
                    break;
                case H264NalUnitType.Pps:
                    pps ??= nal;
                    break;
            }
        }

        if (sps is not null && pps is not null)
        {
            ApplyParameterSets(sps, pps, dts, configFrameKeyFlag: false);
            Log(RtspLogLevel.Info, $"RTSP 从带内数据补齐 SPS/PPS {_key} {_source.Video.Width}x{_source.Video.Height}");
        }
    }

    private void ApplyParameterSets(byte[] sps, byte[] pps, long dts, bool configFrameKeyFlag)
    {
        if (_source is null)
        {
            return;
        }

        var config = new H264Config { Sps = sps, Pps = pps };
        _source.Video.SetH264Config(sps, pps);

        if (H264SpsParser.TryParse(sps, out var info, out _) && info is not null)
        {
            _source.Video.SetVideoSize(info.Width, info.Height);
        }

        var flags = FrameFlags.Config | (configFrameKeyFlag ? FrameFlags.Key : FrameFlags.None);
        _source.AddFrame(MediaFrame.Create(
            config.BuildAnnexBParameterSets(),
            TrackType.Video,
            CodecId.H264,
            dts,
            dts,
            flags));
    }

    private async Task StopUdpAsync()
    {
        var cts = _udpCts;
        _udpCts = null;

        cts?.Cancel();

        foreach (var task in new[] { _udpRtpTask, _udpRtcpTask })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _udpRtpTask = null;
        _udpRtcpTask = null;
        _udpRtp?.Dispose();
        _udpRtcp?.Dispose();
        _udpRtp = null;
        _udpRtcp = null;
        cts?.Dispose();
    }

    private MediaSessionInfo? RegisterSession(StreamKey key)
    {
        var token = _lifetimeCts;
        if (_sessions is null || token is null)
        {
            return null;
        }

        return _sessions.Register(
            "rtsp",
            MediaSessionType.Publisher,
            key,
            _remoteEndPoint?.Address.ToString(),
            _remoteEndPoint?.Port ?? 0,
            _userAgent,
            token.Cancel);
    }

    private async Task<bool> AuthorizeAsync(
        StreamKey key,
        MediaSessionInfo? session,
        RtspRequest request,
        CancellationToken cancellationToken)
    {
        if (_hook is null)
        {
            return true;
        }

        var context = new MediaHookContext(
            "rtsp",
            MediaHookType.Publish,
            key,
            session?.Id ?? string.Empty,
            _remoteEndPoint?.Address.ToString(),
            _remoteEndPoint?.Port ?? 0,
            MediaHookParams.Parse(request.Uri),
            request.Header("User-Agent") ?? _userAgent);

        var result = await _hook(context, cancellationToken).ConfigureAwait(false);
        if (result.Allow)
        {
            return true;
        }

        Log(RtspLogLevel.Warning, $"RTSP 推流被 hook 拒绝 {key}: {result.Message}");
        return false;
    }

    private void CloseSource()
    {
        if (_publishSession is { } session)
        {
            _publishSession = null;
            _sessions?.Unregister(session.Id);
        }

        var source = _source;
        _source = null;
        if (source is null)
        {
            return;
        }

        _registry.Remove(_key, out _);
        Log(RtspLogLevel.Info, $"RTSP 结束推流 {_key}");
    }

    private string SessionHeader => $"{_sessionId};timeout={_options.SessionTimeoutSeconds}";

    private Task WriteErrorAsync(RtspRequest request, int code, string reason, CancellationToken cancellationToken) =>
        WriteResponseAsync(request, code, reason, null, null, cancellationToken);

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

    private void Log(RtspLogLevel level, string message) => _log?.Invoke(level, message);
}
