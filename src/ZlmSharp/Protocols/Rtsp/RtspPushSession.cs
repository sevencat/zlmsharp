using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ZlmSharp.Codecs;
using ZlmSharp.Core;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>
/// RTSP 推流会话（ANNOUNCE → SETUP → RECORD）：
/// 解析 SDP 的 H.264 视频轨与 AAC（MPEG4-GENERIC）音频轨，接收 RTP（TCP interleaved 或 UDP），
/// 解包成 Annex-B 视频 / 裸 AAC 帧写入 <see cref="MediaSource"/>，并周期回 RTCP RR。
/// B 帧按无重排处理（时间戳直接当 DTS）。
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
    private readonly string _sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    private readonly List<TrackReceiver> _tracks = [];

    private MediaSource? _source;
    private StreamKey _key;
    private string _baseUri = string.Empty;
    private bool _recording;
    private bool _configFrameSent;
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _rtcpCts;
    private Task? _rtcpTask;
    private MediaSessionInfo? _publishSession;

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
                else if (_recording && item.Payload is { Length: > 0 } payload)
                {
                    ProcessInterleaved(item.Channel, payload);
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
            await StopRtcpAsync().ConfigureAwait(false);
            await StopTransportsAsync().ConfigureAwait(false);
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
        if (!SdpParser.TryParse(sdp, out var description, out var error) || description is null)
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

        _tracks.Clear();

        if (description.Video is { } video)
        {
            _tracks.Add(new TrackReceiver(video, isAudio: false));
            source.Video.SetCodec(video.Codec);

            if (video.HasParameterSets)
            {
                if (video.IsH265)
                {
                    ApplyH265ParameterSets(video.Vps!, video.Sps!, video.Pps!, 0, configFrameKeyFlag: true);
                }
                else
                {
                    ApplyParameterSets(video.Sps!, video.Pps!, 0, configFrameKeyFlag: true);
                }

                Log(RtspLogLevel.Info, $"RTSP 收到推流配置 {key} {video.Encoding} {source.Video.Width}x{source.Video.Height}");
            }
            else
            {
                Log(RtspLogLevel.Info, $"RTSP 开始推流 {key}（等待带内参数集）");
            }
        }

        if (description.Audio is { } audio)
        {
            _tracks.Add(new TrackReceiver(audio, isAudio: true));
            source.Audio.SetCodec(CodecId.Aac);
            source.Audio.SetAudioInfo(audio.ClockRate, audio.Channels);

            if (audio.AudioSpecificConfig is { Length: > 0 } asc)
            {
                ApplyAudioConfig(asc, 0);
                Log(
                    RtspLogLevel.Info,
                    $"RTSP 收到音频配置 {key} {audio.ClockRate}Hz/{audio.Channels}ch asc={Convert.ToHexString(asc)}");
            }
            else
            {
                Log(RtspLogLevel.Warning, $"RTSP 音频轨缺少 config（ASC），音频可能在录制/转封装时不可用：{key}");
            }
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
                StartRtcp(cancellationToken);
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
        var receiver = ResolveReceiver(request.Uri);
        if (receiver is null)
        {
            Log(RtspLogLevel.Warning, $"RTSP 推流 SETUP 找不到对应轨道: {request.Uri}");
            await WriteResponseAsync(request, 454, "Session Not Found", null, null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var isTcp = transport.Contains("RTP/AVP/TCP", StringComparison.OrdinalIgnoreCase) ||
                    transport.Contains("interleaved=", StringComparison.OrdinalIgnoreCase);

        if (isTcp)
        {
            if (!TryParsePortPair(transport, "interleaved=", out var rtpChannel, out var rtcpChannel))
            {
                rtpChannel = receiver.DefaultRtpChannel;
                rtcpChannel = rtpChannel + 1;
            }

            receiver.RtpChannel = rtpChannel;
            receiver.RtcpChannel = rtcpChannel;

            await StopUdpForAsync(receiver).ConfigureAwait(false);
            await WriteResponseAsync(
                request,
                200,
                "OK",
                [
                    ("Transport", $"RTP/AVP/TCP;unicast;interleaved={rtpChannel}-{rtcpChannel};ssrc={Ssrc:x8}"),
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

        if (!TryStartUdp(receiver, clientRtcpPort, cancellationToken, out var serverRtpPort, out var serverRtcpPort))
        {
            await WriteResponseAsync(request, 461, "Unsupported Transport", null, null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        Log(
            RtspLogLevel.Debug,
            $"RTSP 推流 UDP {receiver.Track.Kind} client_port={clientRtpPort}-{clientRtcpPort} " +
            $"server_port={serverRtpPort}-{serverRtcpPort}");

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

    private TrackReceiver? ResolveReceiver(string uri)
    {
        var trackId = ParseTrackId(uri);
        if (trackId is { } id)
        {
            var matched = _tracks.FirstOrDefault(receiver => receiver.TrackId == id);
            if (matched is not null)
            {
                return matched;
            }
        }

        // 没有 trackId（或对不上）：按 SDP 顺序分配还没配置传输的轨道
        return _tracks.FirstOrDefault(receiver => !receiver.Configured);
    }

    /// <summary>从 <c>.../trackID=1</c> 或 <c>.../streamid=1</c> 取轨道号。</summary>
    private static int? ParseTrackId(string uri)
    {
        var path = uri;
        var query = path.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            path = path[..query];
        }

        var segment = path.TrimEnd('/');
        var slash = segment.LastIndexOf('/');
        if (slash < 0)
        {
            return null;
        }

        var last = segment[(slash + 1)..];
        foreach (var prefix in new[] { "trackID=", "streamid=" })
        {
            if (last.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(last[prefix.Length..], out var id))
            {
                return id;
            }
        }

        return null;
    }

    private uint Ssrc { get; } = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);

    private bool TryStartUdp(
        TrackReceiver receiver,
        int clientRtcpPort,
        CancellationToken cancellationToken,
        out int rtpPort,
        out int rtcpPort)
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

        if (_remoteEndPoint is null)
        {
            Log(RtspLogLevel.Warning, "RTSP 推流 UDP 缺少对端地址，无法发送 RTCP");
            rtp.Dispose();
            rtcp.Dispose();
            return false;
        }

        receiver.DisposeUdp();
        receiver.UdpRtp = rtp;
        receiver.UdpRtcp = rtcp;
        receiver.RtcpTarget = new IPEndPoint(_remoteEndPoint.Address, clientRtcpPort);
        receiver.UdpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = receiver.UdpCts.Token;
        receiver.UdpRtpTask = Task.Run(() => UdpRtpLoopAsync(receiver, rtp, token), CancellationToken.None);
        receiver.UdpRtcpTask = Task.Run(() => UdpRtcpLoopAsync(receiver, rtcp, token), CancellationToken.None);
        return true;
    }

    private async Task UdpRtpLoopAsync(TrackReceiver receiver, UdpClient udp, CancellationToken cancellationToken)
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

            if (_remoteEndPoint is { } remote && !result.RemoteEndPoint.Address.Equals(remote.Address))
            {
                continue;
            }

            if (_recording)
            {
                ProcessRtp(receiver, result.Buffer);
            }
        }
    }

    private async Task UdpRtcpLoopAsync(TrackReceiver receiver, UdpClient udp, CancellationToken cancellationToken)
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

            if (_remoteEndPoint is { } remote && !result.RemoteEndPoint.Address.Equals(remote.Address))
            {
                continue;
            }

            ProcessRtcp(receiver, result.Buffer);
        }
    }

    /// <summary>TCP interleaved 数据按通道分派：RTP 通道喂解包，RTCP 通道更新 SR 信息。</summary>
    private void ProcessInterleaved(int channel, byte[] payload)
    {
        foreach (var receiver in _tracks)
        {
            if (channel == receiver.RtpChannel)
            {
                ProcessRtp(receiver, payload);
                return;
            }

            if (channel == receiver.RtcpChannel)
            {
                ProcessRtcp(receiver, payload);
                return;
            }
        }
    }

    private void ProcessRtp(TrackReceiver receiver, byte[] packet)
    {
        if (_source is null || !RtpPacketHeader.TryParse(packet, out var header))
        {
            return;
        }

        if (header.PayloadType != receiver.Track.PayloadType)
        {
            return;
        }

        if (receiver.LastSequence is { } last)
        {
            var diff = (ushort)(header.SequenceNumber - last);
            if (diff is 0 or > 0x8000)
            {
                return;
            }
        }

        receiver.LastSequence = header.SequenceNumber;
        TrackArrival(receiver, header);

        if (receiver.Track.Kind == SdpMediaKind.Video)
        {
            ProcessVideo(receiver, packet, header);
        }
        else
        {
            ProcessAudio(receiver, packet, header);
        }
    }

    private void ProcessVideo(TrackReceiver receiver, byte[] packet, RtpPacketHeader header)
    {
        if (_source is null ||
            !receiver.Video!.Push(
                packet.AsSpan(header.PayloadOffset, header.PayloadLength),
                header.Marker,
                out var accessUnit,
                out var keyFrame) ||
            accessUnit is null)
        {
            return;
        }

        var dts = ToDts(receiver, header.Timestamp, clockRate: 90_000);
        EnsureInBandConfig(accessUnit, dts);

        _source.AddFrame(MediaFrame.Create(
            accessUnit,
            TrackType.Video,
            receiver.Track.Codec,
            dts,
            dts,
            keyFrame ? FrameFlags.Key : FrameFlags.None));
    }

    private void ProcessAudio(TrackReceiver receiver, byte[] packet, RtpPacketHeader header)
    {
        if (_source is null ||
            !receiver.Audio!.Push(packet.AsSpan(header.PayloadOffset, header.PayloadLength), out var accessUnit) ||
            accessUnit is null)
        {
            return;
        }

        var dts = ToDts(receiver, header.Timestamp, receiver.Track.ClockRate);
        _source.AddFrame(MediaFrame.Create(
            accessUnit,
            TrackType.Audio,
            CodecId.Aac,
            dts,
            dts,
            FrameFlags.None));
    }

    /// <summary>RTCP：只关心发送方 SR（拿 SSRC 与 NTP，用于 RR 的 LSR/DLSR）。</summary>
    private void ProcessRtcp(TrackReceiver receiver, byte[] packet)
    {
        foreach (var (_, payload) in RtcpPacket.Enumerate(packet))
        {
            if (!RtcpPacket.TryParseSenderReport(payload.Span, out var ssrc, out var ntpMiddle))
            {
                continue;
            }

            receiver.SourceSsrc = ssrc;
            receiver.LastSrNtpMiddle = ntpMiddle;
            receiver.LastSrAt = DateTime.UtcNow;
        }
    }

    private void TrackArrival(TrackReceiver receiver, RtpPacketHeader header)
    {
        var now = Stopwatch.GetTimestamp();

        if (receiver.LastArrivalTicks != 0 && receiver.LastRtpTimestamp is { } lastTimestamp)
        {
            var rtpDelta = (long)(uint)(header.Timestamp - lastTimestamp);
            var arrivalDelta = (now - receiver.LastArrivalTicks) * receiver.Track.ClockRate / Stopwatch.Frequency;
            var difference = Math.Abs(rtpDelta - arrivalDelta);
            receiver.Jitter += (uint)((difference - receiver.Jitter) / 16);
        }

        receiver.LastRtpTimestamp = header.Timestamp;
        receiver.LastArrivalTicks = now;

        receiver.PacketsReceived++;
        if (receiver.PacketsReceived == 1)
        {
            receiver.FirstSequence = header.SequenceNumber;
            receiver.HighestSequence = header.SequenceNumber;
        }
        else if (IsNewer(header.SequenceNumber, receiver.HighestSequence))
        {
            receiver.HighestSequence = header.SequenceNumber;
        }

        if (receiver.SourceSsrc == 0)
        {
            receiver.SourceSsrc = header.Ssrc;
        }
    }

    private static bool IsNewer(ushort value, ushort reference) => (short)(value - reference) > 0;

    private void StartRtcp(CancellationToken cancellationToken)
    {
        if (_rtcpTask is not null || _tracks.Count == 0)
        {
            return;
        }

        _rtcpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _rtcpCts.Token;
        _rtcpTask = Task.Run(() => RtcpLoopAsync(token), CancellationToken.None);
    }

    private async Task RtcpLoopAsync(CancellationToken cancellationToken)
    {
        var interval = _options.RtcpInterval > TimeSpan.Zero
            ? _options.RtcpInterval
            : TimeSpan.FromSeconds(5);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var receiver in _tracks.ToArray())
            {
                try
                {
                    await SendReceiverReportAsync(receiver, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
                {
                    Log(RtspLogLevel.Debug, $"RTSP RTCP RR 发送失败: {exception.Message}");
                }
            }
        }
    }

    private async Task SendReceiverReportAsync(TrackReceiver receiver, CancellationToken cancellationToken)
    {
        var delay = receiver.LastSrAt is { } at
            ? RtcpReceiverReport.ToDelayUnits(DateTime.UtcNow - at)
            : 0u;

        var lost = (int)Math.Clamp(receiver.ExpectedCount - receiver.PacketsReceived, 0, int.MaxValue);

        var report = RtcpReceiverReport.Build(
            Ssrc,
            receiver.SourceSsrc,
            0,
            lost,
            receiver.HighestSequence,
            receiver.Jitter,
            receiver.LastSrNtpMiddle,
            delay);

        if (receiver.RtcpChannel >= 0)
        {
            await WriteInterleavedAsync(receiver.RtcpChannel, report, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (receiver.UdpRtcp is not null && receiver.RtcpTarget is not null)
        {
            await receiver.UdpRtcp
                .SendAsync(report, receiver.RtcpTarget, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WriteInterleavedAsync(int channel, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        header[0] = (byte)'$';
        header[1] = (byte)channel;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)payload.Length);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task StopRtcpAsync()
    {
        var cts = _rtcpCts;
        var task = _rtcpTask;
        _rtcpCts = null;
        _rtcpTask = null;

        cts?.Cancel();

        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        cts?.Dispose();
    }

    private async Task StopTransportsAsync()
    {
        foreach (var receiver in _tracks)
        {
            await StopUdpForAsync(receiver).ConfigureAwait(false);
            receiver.DisposeUdp();
        }
    }

    private async Task StopUdpForAsync(TrackReceiver receiver)
    {
        var cts = receiver.UdpCts;
        receiver.UdpCts = null;
        cts?.Cancel();

        foreach (var task in new[] { receiver.UdpRtpTask, receiver.UdpRtcpTask })
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

        receiver.UdpRtpTask = null;
        receiver.UdpRtcpTask = null;
        receiver.DisposeUdp();
        cts?.Dispose();
    }

    private long ToDts(TrackReceiver receiver, uint timestamp, int clockRate)
    {
        if (receiver.BaseTimestamp is null)
        {
            receiver.BaseTimestamp = timestamp;
            receiver.LastDts = 0;
            return 0;
        }

        var delta = unchecked(timestamp - receiver.BaseTimestamp.Value);
        if (delta > 0x80000000)
        {
            // 时间戳回退（B 帧/乱序）：按无重排处理，保持单调
            return receiver.LastDts;
        }

        var dts = (long)delta * 1000 / Math.Max(1, clockRate);
        if (dts < receiver.LastDts)
        {
            dts = receiver.LastDts;
        }

        receiver.LastDts = dts;
        return dts;
    }

    private void EnsureInBandConfig(byte[] accessUnit, long dts)
    {
        if (_source is null)
        {
            return;
        }

        var track = _source.Video;
        var hevc = track.CodecId == CodecId.H265;
        if (hevc ? track.Vps is not null : track.Sps is not null)
        {
            return;
        }

        byte[]? vps = null;
        byte[]? sps = null;
        byte[]? pps = null;

        foreach (var nal in AnnexB.Split(accessUnit))
        {
            if (nal.Length < 2)
            {
                continue;
            }

            if (hevc)
            {
                if (!H265NalUnit.TryReadHeader(nal, out var type, out _))
                {
                    continue;
                }

                switch (type)
                {
                    case H265NalUnitType.Vps:
                        vps ??= nal;
                        break;
                    case H265NalUnitType.Sps:
                        sps ??= nal;
                        break;
                    case H265NalUnitType.Pps:
                        pps ??= nal;
                        break;
                }
            }
            else
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
        }

        if (hevc && vps is not null && sps is not null && pps is not null)
        {
            ApplyH265ParameterSets(vps, sps, pps, dts, configFrameKeyFlag: false);
            Log(RtspLogLevel.Info, $"RTSP 从带内数据补齐 VPS/SPS/PPS {_key} {track.Width}x{track.Height}");
            return;
        }

        if (!hevc && sps is not null && pps is not null)
        {
            ApplyParameterSets(sps, pps, dts, configFrameKeyFlag: false);
            Log(RtspLogLevel.Info, $"RTSP 从带内数据补齐 SPS/PPS {_key} {track.Width}x{track.Height}");
        }
    }

    private void ApplyH265ParameterSets(byte[] vps, byte[] sps, byte[] pps, long dts, bool configFrameKeyFlag)
    {
        if (_source is null)
        {
            return;
        }

        var config = new H265Config { Vps = vps, Sps = sps, Pps = pps };
        _source.Video.SetH265Config(vps, sps, pps);

        if (H265SpsParser.TryParse(sps, out var info, out _) && info is not null)
        {
            _source.Video.SetVideoSize(info.Width, info.Height);
        }

        var flags = FrameFlags.Config | (configFrameKeyFlag ? FrameFlags.Key : FrameFlags.None);
        _source.AddFrame(MediaFrame.Create(
            config.BuildAnnexBParameterSets(),
            TrackType.Video,
            CodecId.H265,
            dts,
            dts,
            flags));
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

    private void ApplyAudioConfig(byte[] asc, long dts)
    {
        if (_source is null || _configFrameSent)
        {
            return;
        }

        _configFrameSent = true;
        _source.Audio.SetAudioConfig(asc);
        _source.AddFrame(MediaFrame.Create(
            asc,
            TrackType.Audio,
            CodecId.Aac,
            dts,
            dts,
            FrameFlags.Config));
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
        _tracks.Clear();

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

    /// <summary>一条轨道的接收状态（传输、解包、时间戳、RTCP 统计）。</summary>
    private sealed class TrackReceiver
    {
        public TrackReceiver(SdpMediaTrack track, bool isAudio)
        {
            Track = track;
            IsAudio = isAudio;
            Video = isAudio
                ? null
                : track.IsH265
                    ? new H265RtpDepacketizer()
                    : new H264RtpDepacketizer();
            Audio = isAudio ? new AacRtpDepacketizer() : null;
        }

        public SdpMediaTrack Track { get; }

        public bool IsAudio { get; }

        public IVideoRtpDepacketizer? Video { get; }

        public AacRtpDepacketizer? Audio { get; }

        public int TrackId => Track.TrackId;

        public int DefaultRtpChannel => IsAudio ? 2 : 0;

        public int RtpChannel { get; set; } = -1;

        public int RtcpChannel { get; set; } = -1;

        public bool Configured => RtpChannel >= 0 || UdpRtp is not null;

        public UdpClient? UdpRtp { get; set; }

        public UdpClient? UdpRtcp { get; set; }

        public IPEndPoint? RtcpTarget { get; set; }

        public CancellationTokenSource? UdpCts { get; set; }

        public Task? UdpRtpTask { get; set; }

        public Task? UdpRtcpTask { get; set; }

        public ushort? LastSequence { get; set; }

        public uint? BaseTimestamp { get; set; }

        public long LastDts { get; set; }

        // RTCP 接收统计
        public uint SourceSsrc { get; set; }

        public ushort HighestSequence { get; set; }

        public ushort FirstSequence { get; set; }

        public uint PacketsReceived { get; set; }

        public long ExpectedCount => FirstSequence == 0 && PacketsReceived == 0
            ? 0
            : (ushort)(HighestSequence - FirstSequence) + 1;

        public uint Jitter { get; set; }

        public long LastArrivalTicks { get; set; }

        public uint? LastRtpTimestamp { get; set; }

        public uint LastSrNtpMiddle { get; set; }

        public DateTime? LastSrAt { get; set; }

        public void DisposeUdp()
        {
            UdpRtp?.Dispose();
            UdpRtcp?.Dispose();
            UdpRtp = null;
            UdpRtcp = null;
        }
    }
}
