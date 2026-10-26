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

    private MediaSource? _playSource;
    private MediaSink? _playSink;
    private CancellationTokenSource? _playCts;
    private Task? _playTask;
    private FlvMuxer? _playMuxer;
    private uint _playStreamId = RtmpConstants.DefaultStreamId;

    public RtmpSession(
        Stream stream,
        MediaSourceRegistry registry,
        RtmpSessionOptions? options = null,
        RtmpLogHandler? log = null)
    {
        _stream = stream;
        _registry = registry;
        _options = options ?? new RtmpSessionOptions();
        _log = log;
    }

    public StreamKey? Key => _publishing ? _key : null;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                handshakeCts.CancelAfter(_options.HandshakeTimeout);
                try
                {
                    await RtmpHandshake.ServerAsync(_stream, handshakeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Log(RtmpLogLevel.Warning, "RTMP 握手超时");
                    return;
                }
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await _reader.ReadMessageAsync(_stream, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                await HandleMessageAsync(message, cancellationToken).ConfigureAwait(false);
                await MaybeSendAcknowledgementAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidDataException exception)
        {
            Log(RtmpLogLevel.Warning, $"RTMP 协议错误: {exception.Message}");
        }
        catch (IOException exception)
        {
            Log(RtmpLogLevel.Debug, $"RTMP 连接中断: {exception.Message}");
        }
        finally
        {
            CloseSource();
            await StopPlaybackAsync().ConfigureAwait(false);
        }
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
            Span<byte> payload = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(payload, windowSize);
            await SendAsync(RtmpMessageType.WindowAcknowledgementSize, payload.ToArray(), 0, 2, cancellationToken)
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

        OpenSource(streamName.Value);
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

    private void OpenSource(string name)
    {
        CloseSource();

        var (stream, vhost) = SplitVhost(name);
        var app = _app.Length > 0 ? _app : _options.DefaultApp;
        _key = StreamKey.Create(vhost, app, stream);
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

        if (source is null)
        {
            return;
        }

        var key = _key;
        _publishing = false;
        _registry.Remove(key, out _);
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

        var (stream, vhost) = SplitVhost(playName.Value);
        var app = _app.Length > 0 ? _app : _options.DefaultApp;
        var key = StreamKey.Create(vhost, app, stream);

        var source = await WaitForSourceAsync(key, _options.MaxStreamWaitMS, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            Log(RtmpLogLevel.Warning, $"RTMP 播放失败：流不存在 {key}");
            await SendStatusAsync("NetStream.Play.StreamNotFound", $"stream not found: {key}", cancellationToken, _playStreamId)
                .ConfigureAwait(false);
            return;
        }

        var config = await H264ConfigResolver
            .WaitAsync(source, _options.WaitTrackReadyMS, cancellationToken)
            .ConfigureAwait(false);
        if (config is null)
        {
            await SendStatusAsync("NetStream.Play.Failed", "流未就绪（缺少 SPS/PPS）", cancellationToken, _playStreamId)
                .ConfigureAwait(false);
            return;
        }

        var muxer = new FlvMuxer(config, new FlvMetadata
        {
            Width = source.Video.Width,
            Height = source.Video.Height,
            FrameRate = source.Video.Fps > 0 ? source.Video.Fps : 25,
        });

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

        // CreateReader 会立刻把 GOP 快照塞进队列，保证秒开
        var sink = source.CreateReader();
        _playSink = sink;
        _playCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var playToken = _playCts.Token;
        _playTask = Task.Run(() => PlaybackLoopAsync(sink, playToken), playToken);

        Log(RtmpLogLevel.Info, $"RTMP 开始播放 {key}");
    }

    private async Task StopPlaybackAsync()
    {
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
