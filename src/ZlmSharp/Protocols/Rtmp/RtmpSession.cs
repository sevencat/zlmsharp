using System.Buffers;
using System.Buffers.Binary;
using ZlmSharp.Codecs;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Flv;

namespace ZlmSharp.Protocols.Rtmp;

public enum RtmpLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>RTMP 会话日志回调（协议库不依赖 Microsoft.Extensions.Logging）。</summary>
public delegate void RtmpLogHandler(RtmpLogLevel level, string message);

public sealed class RtmpSessionOptions
{
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public string DefaultApp { get; init; } = "live";

    /// <summary>服务端回 Acknowledgement 的字节窗口，0 表示不回。</summary>
    public int AcknowledgementWindow { get; init; } = 2500000;

    /// <summary>服务端上行 chunk 大小（发送前会先发 Set Chunk Size）。</summary>
    public int OutChunkSize { get; init; } = 4096;

    /// <summary>播放时等待流出现在注册表里的时长（毫秒）。</summary>
    public int MaxStreamWaitMS { get; init; } = 5000;

    /// <summary>播放时等待 SPS/PPS 就绪的时长（毫秒）。</summary>
    public int WaitTrackReadyMS { get; init; } = 2000;

    /// <summary>HEVC 播放是否使用 Enhanced RTMP 视频头（关闭则退回 legacy codecId 12）。</summary>
    public bool Enhanced { get; init; } = true;
}

/// <summary>
/// RTMP 服务端会话：握手 → connect/createStream → publish / play。
/// 推流把 FLV 音视频消息转成 Annex-B/裸 AAC 帧写入 <see cref="MediaSource"/>；
/// 播放把 <see cref="MediaSource"/> 的帧按 FLV 负载格式（AVCC）装进 RTMP 消息下发。
/// </summary>
public sealed class RtmpSession
{
    private readonly Stream _stream;
    private readonly MediaSourceRegistry _registry;
    private readonly RtmpSessionOptions _options;
    private readonly RtmpLogHandler? _log;
    private readonly MediaHookHandler? _hook;
    private readonly MediaSessionRegistry? _sessions;
    private readonly string? _remoteIp;
    private readonly int _remotePort;
    private readonly string? _userAgent;
    private readonly RtmpChunkReader _reader = new();
    private readonly RtmpChunkWriter _writer = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ArrayBufferWriter<byte> _playBody = new(256 * 1024);

    private MediaSource? _source;
    private StreamKey _key;
    private FlvFrameIngest? _ingest;
    private string _app = "";
    private uint _streamId = RtmpConstants.DefaultStreamId;
    private bool _publishing;
    private long _acknowledgedBytes;
    private CancellationTokenSource? _lifetimeCts;
    private MediaSessionInfo? _publishSession;
    private MediaSessionInfo? _playSession;

    private MediaSource? _playSource;
    private MediaSink? _playSink;
    private CancellationTokenSource? _playCts;
    private Task? _playTask;
    private FlvMuxer? _playMuxer;
    private uint _playStreamId = RtmpConstants.DefaultStreamId;

    private readonly DateTimeOffset _connectedAt = DateTimeOffset.UtcNow;
    private long _playFrames;
    private long _playBytes;
    private long _sentBytes;

    public RtmpSession(
        Stream stream,
        MediaSourceRegistry registry,
        RtmpSessionOptions? options = null,
        RtmpLogHandler? log = null,
        MediaHookHandler? hook = null,
        MediaSessionRegistry? sessions = null,
        string? remoteIp = null,
        int remotePort = 0,
        string? userAgent = null)
    {
        _stream = stream;
        _registry = registry;
        _options = options ?? new RtmpSessionOptions();
        _log = log;
        _hook = hook;
        _sessions = sessions;
        _remoteIp = remoteIp;
        _remotePort = remotePort;
        _userAgent = userAgent;
    }

    public StreamKey? Key => _publishing ? _key : null;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // 会话级 token：kick_session 通过取消它来强制断开连接
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lifetimeCts = lifetime;
        var token = lifetime.Token;

        try
        {
            using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                handshakeCts.CancelAfter(_options.HandshakeTimeout);
                try
                {
                    await RtmpHandshake.ServerAsync(_stream, handshakeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Log(RtmpLogLevel.Warning, "RTMP 握手超时");
                    return;
                }
            }

            while (!token.IsCancellationRequested)
            {
                var message = await _reader.ReadMessageAsync(_stream, token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                await HandleMessageAsync(message, token).ConfigureAwait(false);
                await MaybeSendAcknowledgementAsync(token).ConfigureAwait(false);
            }
        }
        catch (InvalidDataException exception)
        {
            Log(RtmpLogLevel.Warning, $"RTMP 协议错误 {_key}（{RemoteText}）：{exception.Message}");
        }
        catch (IOException exception)
        {
            // 对端直接关闭 TCP 是最常见的“播放器停了”原因，必须能从日志里看出来
            Log(RtmpLogLevel.Info, $"RTMP 连接被对端断开 {_key}（{RemoteText}）：{exception.Message}");
        }
        finally
        {
            _lifetimeCts = null;
            LogSessionClosed();
            CloseSource();
            await StopPlaybackAsync().ConfigureAwait(false);
        }
    }

    private string RemoteText => $"{_remoteIp ?? "-"}:{_remotePort}";

    private void LogSessionClosed()
    {
        var role = _publishing
            ? "推流"
            : _playSession is not null || _playMuxer is not null || _playFrames > 0
                ? "播放"
                : "空闲";

        var seconds = (DateTimeOffset.UtcNow - _connectedAt).TotalSeconds;

        Log(
            role == "空闲" ? RtmpLogLevel.Debug : RtmpLogLevel.Info,
            $"RTMP 连接关闭（{role} {_key}，远端 {RemoteText}，存活 {seconds:F1}s，"
            + $"播放 {Interlocked.Read(ref _playFrames)} 帧 / {Interlocked.Read(ref _playBytes) / 1024} KB，"
            + $"总发出 {Interlocked.Read(ref _sentBytes) / 1024} KB）");
    }

    private async Task HandleMessageAsync(RtmpMessage message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case RtmpMessageType.CommandAmf0:
                await HandleCommandAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case RtmpMessageType.Video:
                _ingest?.Video(message.Payload, message.Timestamp);
                break;

            case RtmpMessageType.Audio:
                _ingest?.Audio(message.Payload, message.Timestamp);
                break;

            case RtmpMessageType.DataAmf0:
                HandleData(message);
                break;

            default:
                break;
        }
    }

    private async Task HandleCommandAsync(RtmpMessage message, CancellationToken cancellationToken)
    {
        if (!Amf0.TryDecode(message.Payload, out var values, out var error) || values.Count == 0)
        {
            Log(RtmpLogLevel.Warning, $"RTMP 命令解析失败: {error}");
            return;
        }

        if (values[0] is not Amf0String command)
        {
            Log(RtmpLogLevel.Warning, "RTMP 命令名不是字符串");
            return;
        }

        var transactionId = values.Count > 1 && values[1] is Amf0Number number ? number.Value : 0;

        switch (command.Value)
        {
            case "connect":
                await HandleConnectAsync(values, transactionId, cancellationToken).ConfigureAwait(false);
                break;

            case "createStream":
                await SendCommandAsync("_result", transactionId, cancellationToken,
                    Amf0Value.Null,
                    Amf0Value.Number(RtmpConstants.DefaultStreamId)).ConfigureAwait(false);
                _streamId = RtmpConstants.DefaultStreamId;
                break;

            case "releaseStream":
            case "FCPublish":
            case "FCUnpublish":
                await SendCommandAsync("_result", transactionId, cancellationToken, Amf0Value.Null).ConfigureAwait(false);
                break;

            case "publish":
                await HandlePublishAsync(values, message, cancellationToken).ConfigureAwait(false);
                break;

            case "play":
                await StartPlaybackAsync(values, message, cancellationToken).ConfigureAwait(false);
                break;

            case "deleteStream":
            case "closeStream":
                CloseSource();
                await StopPlaybackAsync().ConfigureAwait(false);
                break;

            default:
                Log(RtmpLogLevel.Debug, $"RTMP 命令未处理: {command.Value}");
                break;
        }
    }

    private async Task HandleConnectAsync(
        IReadOnlyList<Amf0Value> values,
        double transactionId,
        CancellationToken cancellationToken)
    {
        var app = values.Count > 2 ? values[2].GetStringProperty("app") : null;
        _app = string.IsNullOrWhiteSpace(app) ? _options.DefaultApp : StripQuery(app);

        var windowSize = (uint)Math.Max(0, _options.AcknowledgementWindow);
        if (windowSize > 0)
        {
            // 用数组而非 stackalloc：C# 12 及以下不允许在 async 方法里用 ref struct
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(payload, windowSize);
            await SendAsync(RtmpMessageType.WindowAcknowledgementSize, payload, 0, 2, cancellationToken)
                .ConfigureAwait(false);

            var bandwidth = new byte[5];
            BinaryPrimitives.WriteUInt32BigEndian(bandwidth, windowSize);
            bandwidth[4] = 2; // dynamic
            await SendAsync(RtmpMessageType.SetPeerBandwidth, bandwidth, 0, 2, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_options.OutChunkSize > 0)
        {
            var setChunkSize = _writer.BuildSetChunkSize(_options.OutChunkSize);
            if (setChunkSize.Length > 0)
            {
                await WriteAsync(setChunkSize, cancellationToken).ConfigureAwait(false);
            }
        }

        await SendCommandAsync("_result", transactionId, cancellationToken,
            Amf0Value.Object(
                ("fmsVer", Amf0Value.String("FMS/3,0,1,123")),
                ("capabilities", Amf0Value.Number(31))),
            Amf0Value.Object(
                ("level", Amf0Value.String("status")),
                ("code", Amf0Value.String("NetConnection.Connect.Success")),
                ("description", Amf0Value.String("Connection succeeded.")),
                ("objectEncoding", Amf0Value.Number(0)))).ConfigureAwait(false);

        Log(RtmpLogLevel.Info, $"RTMP connect app={_app}");
    }

    private async Task HandlePublishAsync(
        IReadOnlyList<Amf0Value> values,
        RtmpMessage message,
        CancellationToken cancellationToken)
    {
        if (values.Count <= 3 || values[3] is not Amf0String streamName || streamName.Value.Length == 0)
        {
            await SendStatusAsync("NetStream.Publish.BadName", "缺少流名", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message.StreamId > 0)
        {
            _streamId = message.StreamId;
        }

        var key = ResolveKey(streamName.Value);
        var session = RegisterSession(MediaSessionType.Publisher, key);

        if (!await AuthorizeAsync(MediaHookType.Publish, key, session, streamName.Value, cancellationToken)
                .ConfigureAwait(false))
        {
            _sessions?.Unregister(session?.Id);
            await SendStatusAsync("NetStream.Publish.Failed", "推流鉴权失败", cancellationToken).ConfigureAwait(false);
            return;
        }

        // 先 OpenSource（内部会 CloseSource 注销旧会话），再登记新会话，避免刚注册就被清掉
        OpenSource(key, session?.Id);
        _publishSession = session;
        await SendStatusAsync("NetStream.Publish.Start", $"Start publishing {_key.Stream}", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendCommandAsync(
        string command,
        double transactionId,
        CancellationToken cancellationToken,
        params Amf0Value[] arguments)
    {
        var values = new Amf0Value[2 + arguments.Length];
        values[0] = Amf0Value.String(command);
        values[1] = Amf0Value.Number(transactionId);
        arguments.CopyTo(values, 2);

        var payload = Amf0.Encode(values);
        await SendAsync(RtmpMessageType.CommandAmf0, payload, _streamId, 3, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendStatusAsync(
        string code,
        string description,
        CancellationToken cancellationToken,
        uint? streamId = null)
    {
        var isError = code.Contains(".Failed", StringComparison.Ordinal) ||
                      code.Contains(".BadName", StringComparison.Ordinal) ||
                      code.Contains(".StreamNotFound", StringComparison.Ordinal);

        var payload = Amf0.Encode(
            Amf0Value.String("onStatus"),
            Amf0Value.Number(0),
            Amf0Value.Null,
            Amf0Value.Object(
                ("level", Amf0Value.String(isError ? "error" : "status")),
                ("code", Amf0Value.String(code)),
                ("description", Amf0Value.String(description))));

        await SendAsync(
                RtmpMessageType.CommandAmf0,
                payload,
                streamId ?? _streamId,
                3,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendUserControlAsync(ushort eventType, uint value, CancellationToken cancellationToken)
    {
        var payload = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(payload, eventType);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(2), value);
        await SendAsync(RtmpMessageType.UserControl, payload, 0, 2, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(
        byte type,
        byte[] payload,
        uint streamId,
        uint chunkStreamId,
        CancellationToken cancellationToken)
    {
        var bytes = _writer.BuildMessage(type, streamId, 0, payload, chunkStreamId);
        await WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        // 播放任务与命令/控制消息会并发写同一个 socket，必须串行化
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _sentBytes, bytes.Length);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task MaybeSendAcknowledgementAsync(CancellationToken cancellationToken)
    {
        if (_options.AcknowledgementWindow <= 0)
        {
            return;
        }

        var bytesRead = _reader.BytesRead;
        if (bytesRead - _acknowledgedBytes < _options.AcknowledgementWindow)
        {
            return;
        }

        _acknowledgedBytes = bytesRead;
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)bytesRead);
        await SendAsync(RtmpMessageType.Acknowledgement, payload, 0, 2, cancellationToken).ConfigureAwait(false);
    }

    private StreamKey ResolveKey(string name)
    {
        var (stream, vhost) = SplitVhost(name);
        var app = _app.Length > 0 ? _app : _options.DefaultApp;
        return StreamKey.Create(vhost, app, StripQuery(stream));
    }

    private MediaSessionInfo? RegisterSession(MediaSessionType type, StreamKey key)
    {
        var token = _lifetimeCts;
        if (_sessions is null || token is null)
        {
            return null;
        }

        return _sessions.Register("rtmp", type, key, _remoteIp, _remotePort, _userAgent, token.Cancel);
    }

    private void ReleasePlaySession()
    {
        if (_playSession is { } session)
        {
            _playSession = null;
            _sessions?.Unregister(session.Id);
        }
    }

    private async Task<bool> AuthorizeAsync(
        MediaHookType type,
        StreamKey key,
        MediaSessionInfo? session,
        string rawName,
        CancellationToken cancellationToken)
    {
        if (_hook is null)
        {
            return true;
        }

        var context = new MediaHookContext(
            "rtmp",
            type,
            key,
            session?.Id ?? string.Empty,
            _remoteIp,
            _remotePort,
            MediaHookParams.Parse(rawName),
            _userAgent);

        var result = await _hook(context, cancellationToken).ConfigureAwait(false);
        if (result.Allow)
        {
            return true;
        }

        Log(RtmpLogLevel.Warning, $"RTMP {type} 被 hook 拒绝 {key}: {result.Message}");
        return false;
    }

    private void OpenSource(StreamKey key, string? selfSessionId)
    {
        CloseSource();

        _key = key;

        // 同一 key 已有其它推流者（例如叠加流重启、转推重连）：先踢掉旧连接并摘掉它的 MediaSource，
        // 否则两个会话共享同一个 source，旧连接断开时会把新连接正在写的流关掉，新流再也起不来。
        var kicked = _sessions?.Kick(session =>
            session.Type == MediaSessionType.Publisher &&
            session.Key == key &&
            session.Id != selfSessionId);
        if (kicked is { Count: > 0 })
        {
            Log(RtmpLogLevel.Warning, $"RTMP 新推流顶替 {key}：踢掉旧发布者 {kicked.Count} 个会话");
        }

        if (_registry.TryGet(key, out var stale) && stale is not null)
        {
            _registry.Remove(key, stale);
            Log(RtmpLogLevel.Info, $"RTMP 摘掉旧推流残留的流 {key}（新推流接管）");
        }

        _source = _registry.GetOrCreate(_key, "rtmp");
        _ingest = new FlvFrameIngest(_source, Log);
        _publishing = true;

        Log(RtmpLogLevel.Info, $"RTMP 开始推流 {_key}");
    }

    private void CloseSource()
    {
        var source = _source;
        _source = null;
        _ingest = null;

        if (_publishSession is { } session)
        {
            _publishSession = null;
            _sessions?.Unregister(session.Id);
        }

        if (source is null)
        {
            return;
        }

        var key = _key;
        _publishing = false;
        // 实例匹配：同一 key 若已被新推流者接管，不能把它的 source 摘掉
        _registry.Remove(key, source);
        Log(RtmpLogLevel.Info, $"RTMP 结束推流 {key}");
    }

    private async Task StartPlaybackAsync(
        IReadOnlyList<Amf0Value> values,
        RtmpMessage message,
        CancellationToken cancellationToken)
    {
        if (_publishing)
        {
            await SendStatusAsync("NetStream.Play.Failed", "正在推流，不能同时播放", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (values.Count <= 3 || values[3] is not Amf0String playName || playName.Value.Length == 0)
        {
            await SendStatusAsync("NetStream.Play.StreamNotFound", "缺少流名", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_playTask is not null)
        {
            await StopPlaybackAsync().ConfigureAwait(false);
        }

        _playStreamId = message.StreamId > 0 ? message.StreamId : RtmpConstants.DefaultStreamId;

        var key = ResolveKey(playName.Value);
        var session = RegisterSession(MediaSessionType.Player, key);

        if (!await AuthorizeAsync(MediaHookType.Play, key, session, playName.Value, cancellationToken)
                .ConfigureAwait(false))
        {
            _sessions?.Unregister(session?.Id);
            await SendStatusAsync("NetStream.Play.Failed", "播放鉴权失败", cancellationToken, _playStreamId)
                .ConfigureAwait(false);
            return;
        }

        _playSession = session;
        var stream = key.Stream;

        var source = await WaitForSourceAsync(key, _options.MaxStreamWaitMS, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            ReleasePlaySession();
            Log(RtmpLogLevel.Warning, $"RTMP 播放失败：流不存在 {key}");
            await SendStatusAsync("NetStream.Play.StreamNotFound", $"stream not found: {key}", cancellationToken, _playStreamId)
                .ConfigureAwait(false);
            return;
        }

        var config = await VideoCodecConfigResolver
            .WaitAsync(source, _options.WaitTrackReadyMS, cancellationToken)
            .ConfigureAwait(false);
        if (config is null)
        {
            ReleasePlaySession();
            Log(RtmpLogLevel.Warning, $"RTMP 播放失败：流未就绪（缺少视频参数集）{key}");
            await SendStatusAsync("NetStream.Play.Failed", "流未就绪（缺少视频参数集）", cancellationToken, _playStreamId)
                .ConfigureAwait(false);
            return;
        }

        var muxer = new FlvMuxer(
            config,
            new FlvMetadata
            {
                Width = source.Video.Width,
                Height = source.Video.Height,
                FrameRate = source.Video.Fps > 0 ? source.Video.Fps : 25,
            },
            source.Audio.AudioSpecificConfig ?? default,
            source.Audio.SampleRate,
            source.Audio.Channels,
            _options.Enhanced);

        _playSource = source;
        _playMuxer = muxer;

        // 顺序对齐 ZLM/nginx-rtmp：StreamBegin → Play.Reset → Play.Start → metadata → AVC sequence header
        await SendUserControlAsync(0, _playStreamId, cancellationToken).ConfigureAwait(false);
        await SendStatusAsync("NetStream.Play.Reset", $"Playing and resetting {stream}", cancellationToken, _playStreamId)
            .ConfigureAwait(false);
        await SendStatusAsync("NetStream.Play.Start", $"Playing {stream}", cancellationToken, _playStreamId)
            .ConfigureAwait(false);

        await SendAsync(
                RtmpMessageType.DataAmf0,
                muxer.BuildMetadataBody(),
                _playStreamId,
                5,
                cancellationToken)
            .ConfigureAwait(false);
        await SendAsync(
                RtmpMessageType.Video,
                muxer.BuildAvcConfigBody(),
                _playStreamId,
                6,
                cancellationToken)
            .ConfigureAwait(false);

        // 有音频时必须补发 AAC sequence header：它通常只在推流开始时出现一次，
        // 中途加入的观众拿不到，播放器就会按 FLV 头的默认采样率解码，音画时钟逐渐漂移导致卡顿。
        var audioConfigBody = muxer.BuildAudioConfigBody();
        if (audioConfigBody.Length > 0)
        {
            await SendAsync(
                    RtmpMessageType.Audio,
                    audioConfigBody,
                    _playStreamId,
                    7,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // CreateReader 会立刻把 GOP 快照塞进队列，保证秒开
        var sink = source.CreateReader(livePlayback: true);
        _playSink = sink;
        _playCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var playToken = _playCts.Token;
        _playTask = Task.Run(() => PlaybackLoopAsync(sink, playToken), playToken);

        Log(RtmpLogLevel.Info, $"RTMP 开始播放 {key}");
    }

    private async Task StopPlaybackAsync()
    {
        ReleasePlaySession();

        var cts = _playCts;
        var task = _playTask;
        var sink = _playSink;
        var source = _playSource;

        _playCts = null;
        _playTask = null;
        _playSink = null;
        _playSource = null;
        _playMuxer = null;

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
                Log(RtmpLogLevel.Warning, "RTMP 播放任务停止超时");
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
    }

    private async Task PlaybackLoopAsync(MediaSink sink, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in sink.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    switch (frame.TrackType)
                    {
                        case TrackType.Video:
                            await SendPlayVideoAsync(frame, cancellationToken).ConfigureAwait(false);
                            break;

                        case TrackType.Audio:
                            await SendPlayAudioAsync(frame, cancellationToken).ConfigureAwait(false);
                            break;
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
            Log(RtmpLogLevel.Info, $"RTMP 播放结束 {_key}（{RemoteText}）：本地主动停止，已发 {_playFrames} 帧");
        }
        catch (IOException exception)
        {
            Log(
                RtmpLogLevel.Warning,
                $"RTMP 播放写出失败 {_key}（{RemoteText}）：{exception.Message}；"
                + $"已发 {Interlocked.Read(ref _playFrames)} 帧 / {Interlocked.Read(ref _playBytes) / 1024} KB");
        }
        catch (ObjectDisposedException)
        {
            Log(RtmpLogLevel.Debug, $"RTMP 播放管道已释放 {_key}");
        }
        catch (Exception exception)
        {
            Log(RtmpLogLevel.Error, $"RTMP 播放异常 {_key}（{RemoteText}）：{exception}");
        }
        finally
        {
            var dropped = sink.DroppedFrames;
            if (dropped > 0)
            {
                Log(
                    RtmpLogLevel.Warning,
                    $"RTMP 播放 {_key}（{RemoteText}）队列丢帧 {dropped} 个，客户端消费过慢");
            }

            sink.Dispose();
        }
    }

    private async Task SendPlayVideoAsync(MediaFrame frame, CancellationToken cancellationToken)
    {
        var muxer = _playMuxer;
        if (muxer is null || frame.IsConfig)
        {
            return;
        }

        _playBody.Clear();
        if (muxer.WriteVideoBody(frame, _playBody) == 0)
        {
            return;
        }

        var bytes = _writer.BuildMessage(
            RtmpMessageType.Video,
            _playStreamId,
            muxer.TimestampOf(frame.Dts),
            _playBody.WrittenSpan,
            6);

        await WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

        Interlocked.Increment(ref _playFrames);
        Interlocked.Add(ref _playBytes, bytes.Length);
    }

    private async Task SendPlayAudioAsync(MediaFrame frame, CancellationToken cancellationToken)
    {
        var muxer = _playMuxer;
        if (muxer is null || frame.CodecId != CodecId.Aac || frame.Length == 0)
        {
            return;
        }

        var payload = new byte[2 + frame.Length];
        payload[0] = 0xAF; // AAC / 44kHz / 16bit / stereo
        payload[1] = (byte)(frame.IsConfig ? 0 : 1);
        frame.Data.Span.CopyTo(payload.AsSpan(2));

        var bytes = _writer.BuildMessage(
            RtmpMessageType.Audio,
            _playStreamId,
            muxer.TimestampOf(frame.Dts),
            payload,
            7);

        await WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

        Interlocked.Add(ref _playBytes, bytes.Length);
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

    private void HandleData(RtmpMessage message)
    {
        if (!Amf0.TryDecode(message.Payload, out var values, out _) || values.Count == 0)
        {
            return;
        }

        if (values[0] is Amf0String { Value: "onMetaData" })
        {
            Log(RtmpLogLevel.Debug, $"RTMP onMetaData {_key}");
        }
    }

    private static string StripQuery(string value)
    {
        var index = value.IndexOf('?', StringComparison.Ordinal);
        return index >= 0 ? value[..index] : value;
    }

    /// <summary>把 <c>stream?vhost=xxx</c> 拆成流名与 vhost。</summary>
    private static (string Stream, string? Vhost) SplitVhost(string name)
    {
        var queryIndex = name.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0)
        {
            return (name, null);
        }

        return (name[..queryIndex], ReadQueryValue(name[(queryIndex + 1)..], "vhost"));
    }

    private static string? ReadQueryValue(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=', StringComparison.Ordinal);
            var name = index >= 0 ? pair[..index] : pair;
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return index >= 0 ? Uri.UnescapeDataString(pair[(index + 1)..]) : string.Empty;
        }

        return null;
    }

    private void Log(RtmpLogLevel level, string message) => _log?.Invoke(level, message);
}
