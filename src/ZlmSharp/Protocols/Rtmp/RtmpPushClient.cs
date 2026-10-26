using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using ZlmSharp.Codecs;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Flv;

namespace ZlmSharp.Protocols.Rtmp;

public sealed class RtmpPushOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>从 connect 到 publish 成功（NetStream.Publish.Start）的超时。</summary>
    public TimeSpan PublishTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>回 Acknowledgement 的字节窗口。</summary>
    public int AcknowledgementWindow { get; init; } = 2_500_000;

    /// <summary>发送块大小（提高到 4096 可显著减少大帧的 chunk 头开销）。</summary>
    public int ChunkSize { get; init; } = 4096;
}

/// <summary>
/// RTMP 推流客户端：连远端 → 握手 → connect/createStream/publish → 把本地 <see cref="MediaSource"/>
/// 的 H.264/AAC 作为 FLV 音视频消息推上去。用于「转推」，连不上/被拒会抛异常。
/// </summary>
public static class RtmpPushClient
{
    private const uint CommandChunkStream = 3;
    private const uint AudioChunkStream = 4;
    private const uint DataChunkStream = 5;
    private const uint VideoChunkStream = 6;

    public static async Task RunAsync(
        Uri url,
        MediaSource source,
        RtmpPushOptions? options = null,
        RtmpLogHandler? log = null,
        Action<MediaFrame>? onFrame = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(source);

        if (!RtmpPullClient.TryParseUrl(url, out var host, out var port, out var app, out var stream))
        {
            throw new ArgumentException($"不是合法的 RTMP 推流地址: {url}", nameof(url));
        }

        var config = VideoCodecConfigResolver.Resolve(source)
            ?? throw new InvalidOperationException($"流还没有 H.264 参数集，无法转推：{source.Key}");

        var settings = options ?? new RtmpPushOptions();
        var muxer = new FlvMuxer(
            config,
            new FlvMetadata
            {
                Width = source.Video.Width,
                Height = source.Video.Height,
                FrameRate = source.Video.Fps,
            },
            source.Audio.AudioSpecificConfig ?? default,
            source.Audio.SampleRate,
            source.Audio.Channels);

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
                throw new TimeoutException($"连接 RTMP 超时（{settings.ConnectTimeout.TotalSeconds:F0}s）：{host}:{port}");
            }
        }

        await using var network = client.GetStream();
        await RtmpHandshake.ClientAsync(network, cancellationToken).ConfigureAwait(false);

        var reader = new RtmpChunkReader();
        var writer = new RtmpChunkWriter();
        using var writeGate = new SemaphoreSlim(1, 1);

        if (settings.ChunkSize > 0)
        {
            await SendBytesAsync(writer.BuildSetChunkSize(settings.ChunkSize), writeGate, network, cancellationToken)
                .ConfigureAwait(false);
        }

        uint streamId;
        using (var publishCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            publishCts.CancelAfter(settings.PublishTimeout);
            try
            {
                await SendCommandAsync(
                        network,
                        writer,
                        writeGate,
                        0,
                        "connect",
                        1,
                        publishCts.Token,
                        Amf0Value.Object(
                            ("app", Amf0Value.String(app)),
                            ("type", Amf0Value.String("nonprivate")),
                            ("flashVer", Amf0Value.String("FMLE/3.0 (compatible; FMSc/1.0)")),
                            ("tcUrl", Amf0Value.String($"rtmp://{host}:{port}/{app}")),
                            ("objectEncoding", Amf0Value.Number(0))))
                    .ConfigureAwait(false);

                await ExpectResultAsync(reader, network, "connect", log, publishCts.Token).ConfigureAwait(false);

                await SendCommandAsync(network, writer, writeGate, 0, "createStream", 2, publishCts.Token, Amf0Value.Null)
                    .ConfigureAwait(false);
                var created = await ExpectResultAsync(reader, network, "createStream", log, publishCts.Token).ConfigureAwait(false);
                streamId = created.Count > 3 && created[3] is Amf0Number number && number.Value > 0
                    ? (uint)number.Value
                    : RtmpConstants.DefaultStreamId;

                await SendCommandAsync(
                        network,
                        writer,
                        writeGate,
                        streamId,
                        "publish",
                        4,
                        publishCts.Token,
                        Amf0Value.Null,
                        Amf0Value.String(stream),
                        Amf0Value.String("live"))
                    .ConfigureAwait(false);

                await ExpectPublishStartAsync(reader, network, stream, log, publishCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"RTMP 推流握手/publish 超时（{settings.PublishTimeout.TotalSeconds:F0}s）：{url}");
            }
        }

        // metadata + 参数集
        await SendMessageAsync(
                network,
                writer,
                writeGate,
                RtmpMessageType.DataAmf0,
                0,
                0,
                muxer.BuildMetadataBody(),
                DataChunkStream,
                cancellationToken)
            .ConfigureAwait(false);

        await SendMessageAsync(
                network,
                writer,
                writeGate,
                RtmpMessageType.Video,
                streamId,
                0,
                muxer.BuildAvcConfigBody(),
                VideoChunkStream,
                cancellationToken)
            .ConfigureAwait(false);

        if (muxer.HasAudio)
        {
            await SendMessageAsync(
                    network,
                    writer,
                    writeGate,
                    RtmpMessageType.Audio,
                    streamId,
                    0,
                    muxer.BuildAudioConfigBody(),
                    AudioChunkStream,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        log?.Invoke(RtmpLogLevel.Info, $"RTMP 开始转推 {source.Key} → {url}");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readTask = Task.Run(
            () => ReadLoopAsync(reader, network, writer, writeGate, linked, settings.AcknowledgementWindow, log),
            CancellationToken.None);

        using var sink = source.CreateReader();
        var buffer = new ArrayBufferWriter<byte>(512 * 1024);

        try
        {
            await foreach (var frame in sink.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                try
                {
                    if (frame.IsConfig)
                    {
                        continue;
                    }

                    if (frame.TrackType == TrackType.Video)
                    {
                        buffer.Clear();
                        var length = muxer.WriteVideoBody(frame, buffer);
                        if (length > 0)
                        {
                            await SendMessageAsync(
                                    network,
                                    writer,
                                    writeGate,
                                    RtmpMessageType.Video,
                                    streamId,
                                    muxer.TimestampOf(frame.Dts),
                                    buffer.WrittenMemory,
                                    VideoChunkStream,
                                    linked.Token)
                                .ConfigureAwait(false);
                        }
                    }
                    else if (frame.TrackType == TrackType.Audio)
                    {
                        buffer.Clear();
                        var length = muxer.WriteAudioBody(frame, buffer);
                        if (length > 0)
                        {
                            await SendMessageAsync(
                                    network,
                                    writer,
                                    writeGate,
                                    RtmpMessageType.Audio,
                                    streamId,
                                    muxer.TimestampOf(frame.Dts),
                                    buffer.WrittenMemory,
                                    AudioChunkStream,
                                    linked.Token)
                                .ConfigureAwait(false);
                        }
                    }

                    onFrame?.Invoke(frame);
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
        finally
        {
            linked.Cancel();
            try
            {
                await readTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            log?.Invoke(RtmpLogLevel.Info, $"RTMP 转推结束 {url}");
        }
    }

    private static async Task ReadLoopAsync(
        RtmpChunkReader reader,
        Stream network,
        RtmpChunkWriter writer,
        SemaphoreSlim writeGate,
        CancellationTokenSource linked,
        int acknowledgementWindow,
        RtmpLogHandler? log)
    {
        var window = acknowledgementWindow;
        var acknowledged = 0L;

        try
        {
            while (!linked.IsCancellationRequested)
            {
                var message = await reader.ReadMessageAsync(network, linked.Token).ConfigureAwait(false);
                if (message is null)
                {
                    linked.Cancel();
                    return;
                }

                switch (message.Type)
                {
                    case RtmpMessageType.WindowAcknowledgementSize:
                        if (message.Payload.Length >= 4)
                        {
                            window = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(message.Payload));
                        }

                        break;

                    case RtmpMessageType.CommandAmf0:
                        if (Amf0.TryDecode(message.Payload, out var values, out _) && values.Count > 0 &&
                            values[0] is Amf0String { Value: "_error" })
                        {
                            log?.Invoke(RtmpLogLevel.Warning, "RTMP 转推被远端拒绝（_error）");
                            linked.Cancel();
                        }

                        break;
                }

                if (window > 0 && reader.BytesRead - acknowledged >= window)
                {
                    acknowledged = reader.BytesRead;
                    var payload = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)acknowledged);
                    var bytes = writer.BuildMessage(RtmpMessageType.Acknowledgement, 0, 0, payload, 2);
                    await SendBytesAsync(bytes, writeGate, network, linked.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            log?.Invoke(RtmpLogLevel.Warning, $"RTMP 转推读取失败：{exception.Message}");
            linked.Cancel();
        }
    }

    private static async Task<List<Amf0Value>> ExpectResultAsync(
        RtmpChunkReader reader,
        Stream network,
        string command,
        RtmpLogHandler? log,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await reader.ReadMessageAsync(network, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException($"RTMP 连接在等待 {command} 结果时关闭");

            if (message.Type != RtmpMessageType.CommandAmf0 ||
                !Amf0.TryDecode(message.Payload, out var values, out _) ||
                values.Count < 2 ||
                values[0] is not Amf0String name)
            {
                continue;
            }

            if (name.Value == "_error")
            {
                var detail = values.Count > 3 ? values[3].GetStringProperty("code") ?? values[3].ToString() : "unknown";
                throw new InvalidOperationException($"RTMP {command} 被拒绝：{detail}");
            }

            if (name.Value == "_result")
            {
                log?.Invoke(RtmpLogLevel.Debug, $"RTMP {command} 成功");
                return values;
            }
        }
    }

    private static async Task ExpectPublishStartAsync(
        RtmpChunkReader reader,
        Stream network,
        string stream,
        RtmpLogHandler? log,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await reader.ReadMessageAsync(network, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("RTMP 连接在等待 publish 开始时关闭");

            if (message.Type != RtmpMessageType.CommandAmf0 ||
                !Amf0.TryDecode(message.Payload, out var values, out _) ||
                values.Count < 2 ||
                values[0] is not Amf0String name)
            {
                continue;
            }

            if (name.Value == "_error")
            {
                var detail = values.Count > 3 ? values[3].GetStringProperty("code") ?? values[3].ToString() : "unknown";
                throw new InvalidOperationException($"RTMP publish 被拒绝：{detail}");
            }

            if (name.Value != "onStatus" || values.Count < 4)
            {
                continue;
            }

            var code = values[3].GetStringProperty("code");
            switch (code)
            {
                case "NetStream.Publish.Start":
                    log?.Invoke(RtmpLogLevel.Debug, $"RTMP {stream} {code}");
                    return;

                case "NetStream.Publish.BadName":
                case "NetStream.Publish.Denied":
                case "NetStream.Publish.Failed":
                    throw new InvalidOperationException($"RTMP publish 失败：{code}（{stream}）");
            }
        }
    }

    private static async Task SendCommandAsync(
        Stream network,
        RtmpChunkWriter writer,
        SemaphoreSlim writeGate,
        uint streamId,
        string command,
        double transactionId,
        CancellationToken cancellationToken,
        params Amf0Value[] arguments)
    {
        var values = new List<Amf0Value>(arguments.Length + 2)
        {
            Amf0Value.String(command),
            Amf0Value.Number(transactionId),
        };
        values.AddRange(arguments);

        var bytes = writer.BuildMessage(
            RtmpMessageType.CommandAmf0,
            streamId,
            0,
            Amf0.Encode([.. values]),
            CommandChunkStream);

        await SendBytesAsync(bytes, writeGate, network, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendMessageAsync(
        Stream network,
        RtmpChunkWriter writer,
        SemaphoreSlim writeGate,
        byte type,
        uint streamId,
        uint timestamp,
        ReadOnlyMemory<byte> payload,
        uint chunkStreamId,
        CancellationToken cancellationToken)
    {
        var bytes = writer.BuildMessage(type, streamId, timestamp, payload.Span, chunkStreamId);
        await SendBytesAsync(bytes, writeGate, network, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendBytesAsync(
        byte[] bytes,
        SemaphoreSlim writeGate,
        Stream network,
        CancellationToken cancellationToken)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await network.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await network.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }
}
