using System.Buffers.Binary;
using System.Net.Sockets;
using ZlmSharp.Core;

namespace ZlmSharp.Protocols.Rtmp;

public sealed class RtmpPullOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>从 connect 到 play 开始（收到 NetStream.Play.Start）的超时。</summary>
    public TimeSpan PlayTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>回 Acknowledgement 的字节窗口，0 表示不回。</summary>
    public int AcknowledgementWindow { get; init; } = 2_500_000;
}

/// <summary>
/// RTMP 拉流客户端：连远端 → 握手 → connect/createStream/play → 把收到的 FLV 音视频负载
/// 通过 <see cref="FlvFrameIngest"/> 写进本地 <see cref="MediaSource"/>。
/// 只做播放（拉流代理），不支持认证参数之外的扩展命令。
/// </summary>
public static class RtmpPullClient
{
    private const uint CommandChunkStream = 3;

    /// <summary>解析 <c>rtmp://host[:port]/app[/sub]/stream</c>。</summary>
    public static bool TryParseUrl(Uri url, out string host, out int port, out string app, out string stream)
    {
        host = string.Empty;
        port = 0;
        app = string.Empty;
        stream = string.Empty;

        if (!string.Equals(url.Scheme, "rtmp", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(url.Host))
        {
            return false;
        }

        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        host = url.Host;
        port = url.IsDefaultPort ? 1935 : url.Port;
        stream = Uri.UnescapeDataString(segments[^1]);
        app = string.Join('/', segments[..^1].Select(Uri.UnescapeDataString));
        return app.Length > 0 && stream.Length > 0;
    }

    /// <summary>拉取直到远端结束/出错；返回时不会自动关闭 <paramref name="source"/>（由调用方决定）。</summary>
    public static async Task RunAsync(
        Uri url,
        MediaSource source,
        RtmpPullOptions? options = null,
        RtmpLogHandler? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(source);

        if (!TryParseUrl(url, out var host, out var port, out var app, out var stream))
        {
            throw new ArgumentException($"不是合法的 RTMP 拉流地址: {url}", nameof(url));
        }

        var settings = options ?? new RtmpPullOptions();
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

        await SendCommandAsync(
                network,
                writer,
                0,
                "connect",
                1,
                cancellationToken,
                Amf0Value.Object(
                    ("app", Amf0Value.String(app)),
                    ("type", Amf0Value.String("nonprivate")),
                    ("flashVer", Amf0Value.String("FMLE/3.0 (compatible; FMSc/1.0)")),
                    ("tcUrl", Amf0Value.String($"rtmp://{host}:{port}/{app}"))))
            .ConfigureAwait(false);

        var streamId = RtmpConstants.DefaultStreamId;
        using (var playCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            playCts.CancelAfter(settings.PlayTimeout);
            try
            {
                await ExpectResultAsync(reader, network, "connect", log, playCts.Token).ConfigureAwait(false);

                await SendCommandAsync(network, writer, 0, "createStream", 2, playCts.Token, Amf0Value.Null)
                    .ConfigureAwait(false);
                var created = await ExpectResultAsync(reader, network, "createStream", log, playCts.Token).ConfigureAwait(false);
                streamId = created.Count > 3 && created[3] is Amf0Number number && number.Value > 0
                    ? (uint)number.Value
                    : RtmpConstants.DefaultStreamId;

                await SendCommandAsync(
                        network,
                        writer,
                        streamId,
                        "play",
                        3,
                        playCts.Token,
                        Amf0Value.Null,
                        Amf0Value.String(stream),
                        Amf0Value.Number(-2))
                    .ConfigureAwait(false);

                await ExpectPlayStartAsync(reader, network, stream, log, playCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"RTMP 拉流握手/play 超时（{settings.PlayTimeout.TotalSeconds:F0}s）：{url}");
            }
        }

        log?.Invoke(RtmpLogLevel.Info, $"RTMP 开始拉流 {url}");

        var ingest = new FlvFrameIngest(source, log);
        var window = settings.AcknowledgementWindow;
        var acknowledged = 0L;

        while (true)
        {
            var message = await reader.ReadMessageAsync(network, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                log?.Invoke(RtmpLogLevel.Info, $"RTMP 拉流对端关闭 {url}");
                return;
            }

            switch (message.Type)
            {
                case RtmpMessageType.Video:
                    ingest.Video(message.Payload, message.Timestamp);
                    break;

                case RtmpMessageType.Audio:
                    ingest.Audio(message.Payload, message.Timestamp);
                    break;

                case RtmpMessageType.DataAmf0:
                    log?.Invoke(RtmpLogLevel.Debug, $"RTMP onMetaData {url}");
                    break;

                case RtmpMessageType.CommandAmf0:
                    if (IsPlayEnded(message))
                    {
                        log?.Invoke(RtmpLogLevel.Info, $"RTMP 远端结束播放 {url}");
                        return;
                    }

                    break;

                case RtmpMessageType.WindowAcknowledgementSize:
                    if (message.Payload.Length >= 4)
                    {
                        window = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(message.Payload));
                    }

                    break;

                case RtmpMessageType.UserControl:
                    if (IsStreamEof(message))
                    {
                        log?.Invoke(RtmpLogLevel.Info, $"RTMP 远端 StreamEOF {url}");
                        return;
                    }

                    break;
            }

            if (window > 0 && reader.BytesRead - acknowledged >= window)
            {
                acknowledged = reader.BytesRead;
                await SendAcknowledgementAsync(network, writer, acknowledged, cancellationToken).ConfigureAwait(false);
            }
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
            var message = await reader.ReadMessageAsync(network, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                throw new IOException($"RTMP 连接在等待 {command} 结果时关闭");
            }

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

    private static async Task ExpectPlayStartAsync(
        RtmpChunkReader reader,
        Stream network,
        string stream,
        RtmpLogHandler? log,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await reader.ReadMessageAsync(network, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                throw new IOException("RTMP 连接在等待播放开始时关闭");
            }

            if (message.Type != RtmpMessageType.CommandAmf0 ||
                !Amf0.TryDecode(message.Payload, out var values, out _) ||
                values.Count < 4 ||
                values[0] is not Amf0String name)
            {
                continue;
            }

            var code = values[3].GetStringProperty("code");
            switch (code)
            {
                case "NetStream.Play.Start":
                case "NetStream.Play.Reset":
                    log?.Invoke(RtmpLogLevel.Debug, $"RTMP {stream} {code}");
                    return;

                case "NetStream.Play.StreamNotFound":
                case "NetStream.Play.Failed":
                    throw new InvalidOperationException($"RTMP 播放失败：{code}（{stream}）");

                case "NetStream.Play.UnpublishNotify":
                case "NetStream.Play.Stop":
                    throw new IOException($"RTMP 流已结束：{code}（{stream}）");
            }
        }
    }

    private static bool IsPlayEnded(RtmpMessage message)
    {
        if (!Amf0.TryDecode(message.Payload, out var values, out _) || values.Count == 0)
        {
            return false;
        }

        if (values[0] is Amf0String { Value: "onStatus" } && values.Count > 3)
        {
            var code = values[3].GetStringProperty("code");
            return code is "NetStream.Play.Stop" or "NetStream.Play.UnpublishNotify" or "NetStream.Play.StreamNotFound";
        }

        return values[0] is Amf0String { Value: "close" or "onClose" } or Amf0String { Value: "deleteStream" };
    }

    private static bool IsStreamEof(RtmpMessage message)
    {
        // User Control: 0=StreamBegin, 1=StreamEOF, 2=StreamDry, 3=SetBufferLength
        return message.Payload.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(message.Payload) == 1;
    }

    private static async Task SendAcknowledgementAsync(
        Stream network,
        RtmpChunkWriter writer,
        long bytesRead,
        CancellationToken cancellationToken)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)bytesRead);
        var bytes = writer.BuildMessage(RtmpMessageType.Acknowledgement, 0, 0, payload, 2);
        await network.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await network.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendCommandAsync(
        Stream network,
        RtmpChunkWriter writer,
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

        await network.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await network.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
