using System.Buffers;
using System.Net.Sockets;
using ZlmSharp.Codecs;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Tests;

/// <summary>测试用的最小 RTMP 推流客户端（握手 + connect/createStream/publish + AVC 推流）。</summary>
internal sealed class RtmpPublishClient : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly RtmpChunkReader _reader = new();
    private readonly RtmpChunkWriter _writer = new();

    private RtmpPublishClient(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _stream = stream;
    }

    /// <param name="expectedPublishCode">publish 的 onStatus code；被 hook 拒绝时传 <c>NetStream.Publish.Failed</c>。</param>
    public static async Task<RtmpPublishClient> ConnectAsync(
        string host,
        int port,
        string app,
        string stream,
        CancellationToken cancellationToken,
        string expectedPublishCode = "NetStream.Publish.Start")
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, cancellationToken);

        var networkStream = client.GetStream();
        await RtmpHandshake.ClientAsync(networkStream, cancellationToken);

        var publisher = new RtmpPublishClient(client, networkStream);
        try
        {
            await publisher.SendCommandAsync(
                0,
                "connect",
                1,
                cancellationToken,
                Amf0Value.Object(
                    ("app", Amf0Value.String(app)),
                    ("tcUrl", Amf0Value.String($"rtmp://{host}:{port}/{app}"))));
            Assert.Equal("NetConnection.Connect.Success", await publisher.ReadCodeAsync("_result", cancellationToken));

            await publisher.SendCommandAsync(0, "createStream", 2, cancellationToken, Amf0Value.Null);
            await publisher.ReadCodeAsync("_result", cancellationToken);

            await publisher.SendCommandAsync(
                1,
                "publish",
                3,
                cancellationToken,
                Amf0Value.Null,
                Amf0Value.String(stream),
                Amf0Value.String("live"));
            Assert.Equal(expectedPublishCode, await publisher.ReadCodeAsync("onStatus", cancellationToken));

            return publisher;
        }
        catch
        {
            await publisher.DisposeAsync();
            throw;
        }
    }

    public Task SendAvcConfigAsync(
        byte[] sps,
        byte[] pps,
        uint timestamp = 0,
        CancellationToken cancellationToken = default)
    {
        var record = new H264Config { Sps = sps, Pps = pps }.BuildAvcConfigRecord();
        var payload = new byte[5 + record.Length];
        payload[0] = 0x17; // keyframe + AVC
        payload[1] = 0;    // AVC sequence header
        record.CopyTo(payload, 5);

        return SendVideoMessageAsync(payload, timestamp, cancellationToken);
    }

    public Task SendVideoAsync(
        byte[] annexB,
        bool keyFrame,
        uint timestamp,
        int compositionTime = 0,
        CancellationToken cancellationToken = default)
    {
        var buffer = new ArrayBufferWriter<byte>(annexB.Length + 8);
        var count = H264Avcc.ToAvcc(
            AnnexB.Split(annexB).Select(nal => new ReadOnlyMemory<byte>(nal)),
            4,
            buffer);
        Assert.True(count > 0);

        var payload = new byte[5 + buffer.WrittenCount];
        payload[0] = (byte)(((keyFrame ? 1 : 2) << 4) | 7);
        payload[1] = 1; // AVC NALU
        payload[2] = (byte)(compositionTime >> 16);
        payload[3] = (byte)(compositionTime >> 8);
        payload[4] = (byte)compositionTime;
        buffer.WrittenSpan.CopyTo(payload.AsSpan(5));

        return SendVideoMessageAsync(payload, timestamp, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
    }

    private async Task SendCommandAsync(
        uint streamId,
        string command,
        double transactionId,
        CancellationToken cancellationToken,
        params Amf0Value[] arguments)
    {
        var values = new List<Amf0Value> { Amf0Value.String(command), Amf0Value.Number(transactionId) };
        values.AddRange(arguments);

        var bytes = _writer.BuildMessage(
            RtmpMessageType.CommandAmf0,
            streamId,
            0,
            Amf0.Encode([.. values]));

        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private async Task SendVideoMessageAsync(byte[] payload, uint timestamp, CancellationToken cancellationToken)
    {
        var bytes = _writer.BuildMessage(RtmpMessageType.Video, 1, timestamp, payload);
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private async Task<string?> ReadCodeAsync(string expectedCommand, CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await _reader.ReadMessageAsync(_stream, cancellationToken);
            Assert.NotNull(message);

            if (message!.Type != RtmpMessageType.CommandAmf0)
            {
                continue;
            }

            Assert.True(Amf0.TryDecode(message.Payload, out var values, out var error), error);

            if (values.Count == 0 || values[0] is not Amf0String name || name.Value != expectedCommand)
            {
                continue;
            }

            foreach (var value in values.Skip(2))
            {
                if (value.GetStringProperty("code") is { } code)
                {
                    return code;
                }
            }

            return null;
        }
    }
}
