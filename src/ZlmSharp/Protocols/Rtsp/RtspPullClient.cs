using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using ZlmSharp.Codecs;
using ZlmSharp.Core;

namespace ZlmSharp.Protocols.Rtsp;

public sealed class RtspPullOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan DescribeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>保活间隔（GET_PARAMETER），0 表示不发。</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(25);

    public string UserAgent { get; init; } = "zlmsharp-pull";
}

/// <summary>
/// RTSP 拉流客户端（TCP interleaved）：DESCRIBE → SETUP → PLAY，
/// 把远端 H.264 / AAC 写入本地 <see cref="MediaSource"/>。
/// 只支持 TCP 传输（UDP 拉流对代理场景收益低、NAT 问题多）。
/// </summary>
public static class RtspPullClient
{
    /// <summary>解析 <c>rtsp://host[:port]/[vhost/]app/stream[?params]</c>。</summary>
    public static bool TryParseUrl(
        Uri url,
        out string host,
        out int port,
        out string app,
        out string stream,
        out string? vhost)
    {
        host = string.Empty;
        port = 0;
        app = string.Empty;
        stream = string.Empty;
        vhost = null;

        if (!string.Equals(url.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(url.Host))
        {
            return false;
        }

        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        host = url.Host;
        port = url.IsDefaultPort ? 554 : url.Port;
        stream = Uri.UnescapeDataString(segments[^1]);

        if (segments.Length >= 3)
        {
            vhost = Uri.UnescapeDataString(segments[0]);
            app = string.Join('/', segments[1..^1].Select(Uri.UnescapeDataString));
        }
        else if (segments.Length == 2)
        {
            app = Uri.UnescapeDataString(segments[0]);
        }
        else
        {
            // 单段路径（如 rtsp://host:8554/test）：本地 key 由调用方的 app/stream 决定，这里 app 复用该段
            app = stream;
        }

        return app.Length > 0 && stream.Length > 0;
    }

    /// <summary>拉取直到远端结束/出错；不负责关闭 <paramref name="source"/>。</summary>
    public static async Task RunAsync(
        Uri url,
        MediaSource source,
        RtspPullOptions? options = null,
        RtspLogHandler? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(source);

        if (!TryParseUrl(url, out var host, out var port, out _, out _, out _))
        {
            throw new ArgumentException($"不是合法的 RTSP 拉流地址: {url}", nameof(url));
        }

        var settings = options ?? new RtspPullOptions();
        using var client = new TcpClient { NoDelay = true };

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectCts.CancelAfter(settings.ConnectTimeout);
            try
            {
                await client.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"连接 RTSP 超时（{settings.ConnectTimeout.TotalSeconds:F0}s）：{host}:{port}");
            }
        }

        await using var stream = client.GetStream();
        var reader = new RtspPullReader(stream);
        var writeLock = new SemaphoreSlim(1, 1);
        // 请求 URI 不带 userinfo（凭据只放 Authorization 头），
        // 兼容 rtsp://admin:pass@1..@host/... 这类密码带特殊字符的地址
        var baseUri = BuildRequestUri(url);
        var authenticator = RtspAuthenticator.FromUrl(url);
        var cseq = 0;

        async Task<RtspPullResponse> RequestAsync(
            string method,
            string uri,
            IEnumerable<(string Name, string Value)> headers,
            CancellationToken token)
        {
            var text = BuildRequest(++cseq, method, uri, settings.UserAgent, authenticator.GetHeader(method, uri), headers);
            await WriteAsync(stream, writeLock, Encoding.UTF8.GetBytes(text), token).ConfigureAwait(false);
            return await reader.ReadResponseAsync(token).ConfigureAwait(false);
        }

        // DESCRIBE（401 时按 WWW-Authenticate 质询切换 Digest/Basic 后重试一次）
        RtspPullResponse describe;
        using (var describeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            describeCts.CancelAfter(settings.DescribeTimeout);
            describe = await RequestAsync(
                    "DESCRIBE",
                    baseUri,
                    [("Accept", "application/sdp")],
                    describeCts.Token)
                .ConfigureAwait(false);
        }

        if (describe.StatusCode == 401 &&
            authenticator.Challenge(describe.Header("WWW-Authenticate")))
        {
            using (var describeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                describeCts.CancelAfter(settings.DescribeTimeout);
                describe = await RequestAsync(
                        "DESCRIBE",
                        baseUri,
                        [("Accept", "application/sdp")],
                        describeCts.Token)
                    .ConfigureAwait(false);
            }
        }

        if (describe.StatusCode != 200)
        {
            var hint = describe.StatusCode == 401 ? "（用户名/密码错误，或认证方式不支持）" : string.Empty;
            throw new InvalidOperationException($"RTSP DESCRIBE 失败：{describe.StatusCode} {describe.Reason}{hint}");
        }

        if (!SdpParser.TryParse(describe.Body, out var description, out var sdpError) || description is null)
        {
            throw new InvalidOperationException($"RTSP SDP 解析失败：{sdpError}");
        }

        var tracks = new List<PullTrack>();
        var session = string.Empty;
        var channel = 0;

        foreach (var track in new[] { description.Video, description.Audio })
        {
            if (track is null)
            {
                continue;
            }

            var setupUri = CombineUri(baseUri, track.Control);
            var response = await RequestAsync(
                    "SETUP",
                    setupUri,
                    [
                        ("Transport", $"RTP/AVP/TCP;unicast;interleaved={channel}-{channel + 1}"),
                        ("Session", session),
                    ],
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode != 200)
            {
                throw new InvalidOperationException(
                    $"RTSP SETUP 失败（{track.Kind}）：{response.StatusCode} {response.Reason}");
            }

            session = ParseSession(response.Header("Session")) ?? session;
            tracks.Add(new PullTrack(track, channel, channel + 1));
            channel += 2;
        }

        if (tracks.Count == 0)
        {
            throw new InvalidOperationException("RTSP SDP 里没有可用的 H.264 / AAC 轨道");
        }

        var play = await RequestAsync(
                "PLAY",
                baseUri,
                [
                    ("Session", session),
                    ("Range", "npt=0.000-"),
                ],
                cancellationToken)
            .ConfigureAwait(false);

        if (play.StatusCode != 200)
        {
            throw new InvalidOperationException($"RTSP PLAY 失败：{play.StatusCode} {play.Reason}");
        }

        ApplyConfig(source, description);
        log?.Invoke(RtspLogLevel.Info, $"RTSP 拉流已开始 {source.Key} ← {baseUri}");

        using var keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var keepAlive = settings.KeepAliveInterval > TimeSpan.Zero
            ? Task.Run(
                () => KeepAliveAsync(
                    stream,
                    writeLock,
                    settings,
                    authenticator,
                    baseUri,
                    session,
                    settings.KeepAliveInterval,
                    keepAliveCts.Token),
                CancellationToken.None)
            : Task.CompletedTask;

        try
        {
            await ReadLoopAsync(stream, reader, source, tracks, writeLock, log, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            log?.Invoke(RtspLogLevel.Info, $"RTSP 拉流结束 {source.Key} ← {baseUri}");
            keepAliveCts.Cancel();

            try
            {
                using var teardownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var text = BuildRequest(++cseq, "TEARDOWN", baseUri, settings.UserAgent, authenticator.GetHeader("TEARDOWN", baseUri), [("Session", session)]);
                await WriteAsync(stream, writeLock, Encoding.UTF8.GetBytes(text), teardownCts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            try
            {
                await keepAlive.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            writeLock.Dispose();
        }
    }

    private static async Task ReadLoopAsync(
        Stream stream,
        RtspPullReader reader,
        MediaSource source,
        IReadOnlyList<PullTrack> tracks,
        SemaphoreSlim writeLock,
        RtspLogHandler? log,
        CancellationToken cancellationToken)
    {
        var lastReport = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (source.IsClosed)
            {
                log?.Invoke(RtspLogLevel.Info, $"本地流已关闭，结束 RTSP 拉流 {source.Key}");
                return;
            }

            var item = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                log?.Invoke(RtspLogLevel.Info, $"RTSP 拉流对端关闭 {source.Key}");
                return;
            }

            if (item.Response is { } response)
            {
                // 保活的响应/服务器下发的请求（如 ANNOUNCE）直接忽略
                _ = response;
                continue;
            }

            if (item.Interleaved is not { } payload)
            {
                continue;
            }

            var track = tracks.FirstOrDefault(item2 => item2.RtpChannel == payload.Channel);
            if (track is not null)
            {
                ProcessRtp(source, track, payload.Data);
                continue;
            }

            var rtcpTrack = tracks.FirstOrDefault(item2 => item2.RtcpChannel == payload.Channel);
            if (rtcpTrack is not null)
            {
                foreach (var (_, packet) in RtcpPacket.Enumerate(payload.Data))
                {
                    if (RtcpPacket.TryParseSenderReport(packet.Span, out var ssrc, out var ntp))
                    {
                        rtcpTrack.SourceSsrc = ssrc;
                        rtcpTrack.LastSrNtpMiddle = ntp;
                        rtcpTrack.LastSrAt = DateTime.UtcNow;
                    }
                }
            }

            if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
            {
                lastReport = DateTime.UtcNow;
                await SendReceiverReportsAsync(writeLock, stream, tracks, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task SendReceiverReportsAsync(
        SemaphoreSlim writeLock,
        Stream stream,
        IReadOnlyList<PullTrack> tracks,
        CancellationToken cancellationToken)
    {
        foreach (var track in tracks)
        {
            if (track.RtcpChannel < 0 || track.PacketsReceived == 0)
            {
                continue;
            }

            var delay = track.LastSrAt is { } at ? RtcpReceiverReport.ToDelayUnits(DateTime.UtcNow - at) : 0u;
            var report = RtcpReceiverReport.Build(
                track.LocalSsrc,
                track.SourceSsrc,
                0,
                (int)Math.Clamp(track.ExpectedCount - track.PacketsReceived, 0, int.MaxValue),
                track.HighestSequence,
                track.Jitter,
                track.LastSrNtpMiddle,
                delay);

            var header = new byte[4];
            header[0] = (byte)'$';
            header[1] = (byte)track.RtcpChannel;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)report.Length);

            var buffer = new byte[4 + report.Length];
            header.CopyTo(buffer, 0);
            report.CopyTo(buffer, 4);

            await WriteAsync(stream, writeLock, buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ProcessRtp(MediaSource source, PullTrack track, byte[] packet)
    {
        if (!RtpPacketHeader.TryParse(packet, out var header) ||
            header.PayloadType != track.Track.PayloadType)
        {
            return;
        }

        if (track.LastSequence is { } last)
        {
            var diff = (ushort)(header.SequenceNumber - last);
            if (diff is 0 or > 0x8000)
            {
                return;
            }
        }

        track.LastSequence = header.SequenceNumber;
        TrackArrival(track, header);

        if (track.Track.Kind == SdpMediaKind.Video)
        {
            if (!track.Video!.Push(
                    packet.AsSpan(header.PayloadOffset, header.PayloadLength),
                    header.Marker,
                    out var accessUnit,
                    out var keyFrame) ||
                accessUnit is null)
            {
                return;
            }

            var dts = track.ToDts(header.Timestamp, 90_000);
            EnsureInBandConfig(source, accessUnit, dts);

            source.AddFrame(MediaFrame.Create(
                accessUnit,
                TrackType.Video,
                track.Track.Codec,
                dts,
                dts,
                keyFrame ? FrameFlags.Key : FrameFlags.None));
        }
        else
        {
            if (!track.Audio!.Push(packet.AsSpan(header.PayloadOffset, header.PayloadLength), out var accessUnit) ||
                accessUnit is null)
            {
                return;
            }

            var dts = track.ToDts(header.Timestamp, track.Track.ClockRate);
            source.AddFrame(MediaFrame.Create(
                accessUnit,
                TrackType.Audio,
                CodecId.Aac,
                dts,
                dts,
                FrameFlags.None));
        }
    }

    private static void TrackArrival(PullTrack track, RtpPacketHeader header)
    {
        track.PacketsReceived++;
        if (track.PacketsReceived == 1)
        {
            track.FirstSequence = header.SequenceNumber;
            track.HighestSequence = header.SequenceNumber;
        }
        else if ((short)(header.SequenceNumber - track.HighestSequence) > 0)
        {
            track.HighestSequence = header.SequenceNumber;
        }

        if (track.SourceSsrc == 0)
        {
            track.SourceSsrc = header.Ssrc;
        }
    }

    private static void EnsureInBandConfig(MediaSource source, byte[] accessUnit, long dts)
    {
        var hevc = source.Video.CodecId == CodecId.H265;
        if (hevc ? source.Video.Vps is not null : source.Video.Sps is not null)
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
            ApplyH265VideoConfig(source, vps, sps, pps, dts);
            return;
        }

        if (!hevc && sps is not null && pps is not null)
        {
            ApplyVideoConfig(source, sps, pps, dts);
        }
    }

    private static void ApplyConfig(MediaSource source, SdpDescription description)
    {
        if (description.Video is { HasParameterSets: true } video)
        {
            if (video.IsH265)
            {
                ApplyH265VideoConfig(source, video.Vps!, video.Sps!, video.Pps!, 0, withConfigFrame: true);
            }
            else
            {
                ApplyVideoConfig(source, video.Sps!, video.Pps!, 0, withConfigFrame: true);
            }
        }

        if (description.Audio is { } audio)
        {
            source.Audio.SetCodec(CodecId.Aac);
            source.Audio.SetAudioInfo(audio.ClockRate, audio.Channels);

            if (audio.AudioSpecificConfig is { Length: > 0 } asc)
            {
                source.Audio.SetAudioConfig(asc);
                source.AddFrame(MediaFrame.Create(
                    asc,
                    TrackType.Audio,
                    CodecId.Aac,
                    0,
                    0,
                    FrameFlags.Config));
            }
        }
    }

    private static void ApplyVideoConfig(MediaSource source, byte[] sps, byte[] pps, long dts, bool withConfigFrame = true)
    {
        source.Video.SetCodec(CodecId.H264);
        source.Video.SetH264Config(sps, pps);

        if (H264SpsParser.TryParse(sps, out var info, out _) && info is not null)
        {
            source.Video.SetVideoSize(info.Width, info.Height);
        }

        if (!withConfigFrame)
        {
            return;
        }

        var config = new H264Config { Sps = sps, Pps = pps };
        source.AddFrame(MediaFrame.Create(
            config.BuildAnnexBParameterSets(),
            TrackType.Video,
            CodecId.H264,
            dts,
            dts,
            FrameFlags.Config | FrameFlags.Key));
    }

    private static void ApplyH265VideoConfig(
        MediaSource source,
        byte[] vps,
        byte[] sps,
        byte[] pps,
        long dts,
        bool withConfigFrame = true)
    {
        source.Video.SetCodec(CodecId.H265);
        source.Video.SetH265Config(vps, sps, pps);

        if (H265SpsParser.TryParse(sps, out var info, out _) && info is not null)
        {
            source.Video.SetVideoSize(info.Width, info.Height);
        }

        if (!withConfigFrame)
        {
            return;
        }

        var config = new H265Config { Vps = vps, Sps = sps, Pps = pps };
        source.AddFrame(MediaFrame.Create(
            config.BuildAnnexBParameterSets(),
            TrackType.Video,
            CodecId.H265,
            dts,
            dts,
            FrameFlags.Config | FrameFlags.Key));
    }

    private static async Task KeepAliveAsync(
        Stream stream,
        SemaphoreSlim writeLock,
        RtspPullOptions settings,
        RtspAuthenticator authenticator,
        string baseUri,
        string session,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var cseq = 0;
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

            try
            {
                var text = BuildRequest(
                    ++cseq,
                    "GET_PARAMETER",
                    baseUri,
                    settings.UserAgent,
                    authenticator.GetHeader("GET_PARAMETER", baseUri),
                    [("Session", session)]);
                await WriteAsync(stream, writeLock, Encoding.UTF8.GetBytes(text), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    private static string BuildRequest(
        int cseq,
        string method,
        string uri,
        string userAgent,
        string? authorization,
        IEnumerable<(string Name, string Value)> headers)
    {
        var builder = new StringBuilder(256);
        builder.Append(method).Append(' ').Append(uri).Append(" RTSP/1.0\r\n");
        builder.Append("CSeq: ").Append(cseq).Append("\r\n");
        builder.Append("User-Agent: ").Append(userAgent).Append("\r\n");

        if (!string.IsNullOrEmpty(authorization))
        {
            builder.Append("Authorization: ").Append(authorization).Append("\r\n");
        }

        var hasSession = false;
        foreach (var (name, value) in headers)
        {
            if (value.Length == 0)
            {
                continue;
            }

            if (name.Equals("Session", StringComparison.OrdinalIgnoreCase))
            {
                hasSession = true;
            }

            builder.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        _ = hasSession;
        builder.Append("\r\n");
        return builder.ToString();
    }

    internal static (string Username, string Password)? GetCredentials(Uri url)
    {
        if (string.IsNullOrEmpty(url.UserInfo))
        {
            return null;
        }

        var userInfo = Uri.UnescapeDataString(url.UserInfo);
        var colon = userInfo.IndexOf(':');
        return colon < 0
            ? (userInfo, string.Empty)
            : (userInfo[..colon], userInfo[(colon + 1)..]);
    }

    /// <summary>生成不带 userinfo 的请求 URI：<c>rtsp://host[:port]/path[?query]</c>。</summary>
    private static string BuildRequestUri(Uri url)
    {
        var port = url.IsDefaultPort ? string.Empty : $":{url.Port}";
        var pathAndQuery = url.PathAndQuery;
        if (pathAndQuery.Length == 0 || pathAndQuery[0] != '/')
        {
            pathAndQuery = "/" + pathAndQuery;
        }

        return $"{url.Scheme}://{url.Host}{port}{pathAndQuery}";
    }

    private static string CombineUri(string baseUri, string control)
    {
        if (string.IsNullOrEmpty(control))
        {
            return baseUri;
        }

        if (Uri.TryCreate(control, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        var queryIndex = baseUri.IndexOf('?');
        var pathPart = queryIndex < 0 ? baseUri : baseUri[..queryIndex];
        var query = queryIndex < 0 ? string.Empty : baseUri[queryIndex..];
        return pathPart.TrimEnd('/') + "/" + control.TrimStart('/') + query;
    }

    private static string? ParseSession(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var semicolon = header.IndexOf(';');
        var value = (semicolon < 0 ? header : header[..semicolon]).Trim();
        return value.Length == 0 ? null : value;
    }

    private static async Task WriteAsync(
        Stream stream,
        SemaphoreSlim writeLock,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private sealed class PullTrack
    {
        public PullTrack(SdpMediaTrack track, int rtpChannel, int rtcpChannel)
        {
            Track = track;
            RtpChannel = rtpChannel;
            RtcpChannel = rtcpChannel;
            Video = track.Kind != SdpMediaKind.Video
            ? null
            : track.IsH265
                ? new H265RtpDepacketizer()
                : new H264RtpDepacketizer();
            Audio = track.Kind == SdpMediaKind.Audio ? new AacRtpDepacketizer() : null;
            LocalSsrc = (uint)Random.Shared.NextInt64(1, int.MaxValue);
        }

        public SdpMediaTrack Track { get; }

        public int RtpChannel { get; }

        public int RtcpChannel { get; }

        public IVideoRtpDepacketizer? Video { get; }

        public AacRtpDepacketizer? Audio { get; }

        public uint LocalSsrc { get; }

        public ushort? LastSequence { get; set; }

        public uint? BaseTimestamp { get; set; }

        public long LastDts { get; set; }

        public uint SourceSsrc { get; set; }

        public ushort FirstSequence { get; set; }

        public ushort HighestSequence { get; set; }

        public uint PacketsReceived { get; set; }

        public uint Jitter { get; set; }

        public uint LastSrNtpMiddle { get; set; }

        public DateTime? LastSrAt { get; set; }

        public long ExpectedCount => PacketsReceived == 0 ? 0 : (ushort)(HighestSequence - FirstSequence) + 1;

        public long ToDts(uint timestamp, int clockRate)
        {
            if (BaseTimestamp is null)
            {
                BaseTimestamp = timestamp;
                return 0;
            }

            var delta = unchecked(timestamp - BaseTimestamp.Value);
            if (delta > 0x80000000)
            {
                return LastDts;
            }

            var dts = (long)delta * 1000 / Math.Max(1, clockRate);
            if (dts < LastDts)
            {
                dts = LastDts;
            }

            LastDts = dts;
            return dts;
        }
    }
}

internal sealed record RtspPullResponse(
    int StatusCode,
    string Reason,
    IReadOnlyDictionary<string, string> Headers,
    string Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

internal sealed class RtspPullItem
{
    public RtspPullItem(RtspPullResponse? response, (int Channel, byte[] Data)? interleaved)
    {
        Response = response;
        Interleaved = interleaved;
    }

    public RtspPullResponse? Response { get; }

    public (int Channel, byte[] Data)? Interleaved { get; }
}

/// <summary>同时处理 RTSP 响应与 TCP interleaved 帧的读取器（PLAY 之后两者会交错出现）。</summary>
internal sealed class RtspPullReader
{
    private const int MaxHeaderBytes = 64 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;

    public RtspPullReader(Stream stream) => _stream = stream;

    /// <summary>PLAY 之前只读响应。</summary>
    public async Task<RtspPullResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        var statusLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("RTSP 连接已关闭");

        var headers = await ReadHeadersAsync(cancellationToken).ConfigureAwait(false);
        var body = string.Empty;

        if (headers.TryGetValue("Content-Length", out var lengthText) &&
            int.TryParse(lengthText, out var length) &&
            length > 0)
        {
            var bytes = new byte[length];
            await ReadExactAsync(bytes, cancellationToken).ConfigureAwait(false);
            body = Encoding.UTF8.GetString(bytes);
        }

        return ParseResponse(statusLine, headers, body);
    }

    /// <summary>读取一个单元（响应或 interleaved 帧）；连接结束返回 null。</summary>
    public async Task<RtspPullItem?> ReadAsync(CancellationToken cancellationToken)
    {
        var first = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (first < 0)
        {
            return null;
        }

        if (first == '$')
        {
            var header = new byte[3];
            await ReadExactAsync(header, cancellationToken).ConfigureAwait(false);
            var channel = header[0];
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
            var payload = new byte[length];
            await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false);
            return new RtspPullItem(null, (channel, payload));
        }

        var statusLine = (char)first + (await ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty);
        var headers = await ReadHeadersAsync(cancellationToken).ConfigureAwait(false);
        return new RtspPullItem(ParseResponse(statusLine, headers, string.Empty), null);
    }

    private static RtspPullResponse ParseResponse(
        string statusLine,
        IReadOnlyDictionary<string, string> headers,
        string body)
    {
        var parts = statusLine.Split(' ', 3);
        var code = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : 0;
        var reason = parts.Length > 2 ? parts[2] : string.Empty;
        return new RtspPullResponse(code, reason, headers, body);
    }

    private async Task<Dictionary<string, string>> ReadHeadersAsync(CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;

        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null || line.Length == 0)
            {
                return headers;
            }

            total += line.Length;
            if (total > MaxHeaderBytes)
            {
                throw new InvalidDataException("RTSP 响应头过大");
            }

            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(64);
        while (true)
        {
            var current = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (current < 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            if (current == '\n')
            {
                if (builder.Length > 0 && builder[^1] == '\r')
                {
                    builder.Length--;
                }

                return builder.ToString();
            }

            builder.Append((char)current);
        }
    }

    private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_start == _end)
        {
            _end = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _start = 0;
            if (_end == 0)
            {
                return -1;
            }
        }

        return _buffer[_start++];
    }

    private async Task ReadExactAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length && _start < _end)
        {
            destination.Span[read++] = _buffer[_start++];
        }

        while (read < destination.Length)
        {
            var count = await _stream.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new IOException("RTSP 连接已关闭");
            }

            read += count;
        }
    }
}

/// <summary>
/// RTSP 拉流认证：先用 URL 里的 Basic 凭据主动认证，
/// 收到 401 质询（海康等相机常用 Digest）后切换并重试，之后所有请求都带认证头。
/// </summary>
internal sealed class RtspAuthenticator
{
    private readonly (string Username, string Password)? _credentials;
    private readonly string? _basic;
    private DigestChallenge? _digest;
    private uint _nc;

    private RtspAuthenticator((string Username, string Password)? credentials)
    {
        _credentials = credentials;
        _basic = credentials is { } value
            ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{value.Username}:{value.Password}"))
            : null;
    }

    public static RtspAuthenticator FromUrl(Uri url) => new(RtspPullClient.GetCredentials(url));

    /// <summary>收到 401 质询时切换认证方式；返回是否可以重试。</summary>
    public bool Challenge(string? wwwAuthenticate)
    {
        if (string.IsNullOrWhiteSpace(wwwAuthenticate))
        {
            return false;
        }

        var header = wwwAuthenticate.Trim();
        var space = header.IndexOf(' ');
        var scheme = (space < 0 ? header : header[..space]).Trim();

        if (!scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase))
        {
            // Basic 质询：继续用 URL 凭据重试一次
            return _basic is not null;
        }

        var credentials = _credentials;
        if (credentials is null)
        {
            return false;
        }

        _digest = DigestChallenge.Parse(credentials.Value.Username, credentials.Value.Password, header[(space + 1)..]);
        _nc = 0;
        return true;
    }

    public string GetHeader(string method, string uri)
    {
        if (_digest is not null)
        {
            _nc = _nc >= 99999999u ? 1u : _nc + 1;
            return _digest.Build(method, uri, _nc);
        }

        return _basic ?? string.Empty;
    }
}

/// <summary>RTSP Digest 质询参数与 Authorization 头生成（RFC 2069/2617，MD5，qop=auth）。</summary>
internal sealed class DigestChallenge
{
    private readonly string _username;
    private readonly string _password;
    private readonly string _realm;
    private readonly string _nonce;
    private readonly string? _opaque;
    private readonly string _qop;

    private DigestChallenge(string username, string password, string realm, string nonce, string? opaque, string qop)
    {
        _username = username;
        _password = password;
        _realm = realm;
        _nonce = nonce;
        _opaque = opaque;
        _qop = qop;
    }

    public static DigestChallenge Parse(string username, string password, string text)
    {
        var parameters = ParseParameters(text);
        parameters.TryGetValue("realm", out var realm);
        parameters.TryGetValue("nonce", out var nonce);
        parameters.TryGetValue("qop", out var qop);
        parameters.TryGetValue("opaque", out var opaque);
        return new DigestChallenge(username, password, realm ?? string.Empty, nonce ?? string.Empty, opaque, qop ?? string.Empty);
    }

    public string Build(string method, string uri, uint nc)
    {
        // qop 是列表（如 "auth,auth-int"）时选 auth
        var qop = _qop
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(item => item.Equals("auth", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var cnonce = Random.Shared.Next().ToString("x8", CultureInfo.InvariantCulture);

        var ha1 = Md5Hex($"{_username}:{_realm}:{_password}");
        var ha2 = Md5Hex($"{method}:{uri}");
        var response = qop.Length == 0
            ? Md5Hex($"{ha1}:{_nonce}:{ha2}")
            : Md5Hex($"{ha1}:{_nonce}:{nc:00000000}:{cnonce}:{qop}:{ha2}");

        var builder = new StringBuilder(256);
        builder.Append("Digest username=\"").Append(Escape(_username)).Append('"');
        builder.Append(", realm=\"").Append(Escape(_realm)).Append('"');
        builder.Append(", nonce=\"").Append(Escape(_nonce)).Append('"');
        builder.Append(", uri=\"").Append(Escape(uri)).Append('"');
        builder.Append(", response=\"").Append(response).Append('"');

        if (!string.IsNullOrEmpty(_opaque))
        {
            builder.Append(", opaque=\"").Append(Escape(_opaque)).Append('"');
        }

        if (qop.Length > 0)
        {
            builder.Append(", qop=auth");
            builder.Append(", nc=").AppendFormat(CultureInfo.InvariantCulture, "{0:00000000}", nc);
            builder.Append(", cnonce=\"").Append(cnonce).Append('"');
        }

        return builder.ToString();
    }

    /// <summary>解析质询参数，值可能带引号、内容可能含逗号。</summary>
    private static Dictionary<string, string> ParseParameters(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inQuote = false;
        var start = 0;
        var items = new List<string>();

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == ',' && !inQuote)
            {
                items.Add(text[start..i]);
                start = i + 1;
            }
        }

        items.Add(text[start..]);
        foreach (var item in items)
        {
            var eq = item.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = item[..eq].Trim();
            var value = item[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            map[key] = value;
        }

        return map;
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");

    private static string Md5Hex(string value) =>
        Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
