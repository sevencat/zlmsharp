using System.Net.Sockets;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Tests;

/// <summary>测试用的最小 RTMP 播放客户端（握手 + connect/createStream + play）。</summary>
internal sealed class RtmpPlayClient : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly RtmpChunkReader _reader = new();
    private readonly RtmpChunkWriter _writer = new();

    private RtmpPlayClient(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _stream = stream;
    }

    public static async Task<RtmpPlayClient> ConnectAsync(
        string host,
        int port,
        string app,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, cancellationToken);

        var stream = client.GetStream();
        await RtmpHandshake.ClientAsync(stream, cancellationToken);

        var session = new RtmpPlayClient(client, stream);
        try
        {
            await session.SendCommandAsync(
                0,
                "connect",
                1,
                cancellationToken,
                Amf0Value.Object(
                    ("app", Amf0Value.String(app)),
                    ("tcUrl", Amf0Value.String($"rtmp://{host}:{port}/{app}"))));

            var connectResult = await session.ReadNextCommandAsync(cancellationToken);
            Assert.Equal("_result", Assert.IsType<Amf0String>(connectResult[0]).Value);
            Assert.Equal("NetConnection.Connect.Success", connectResult[3].GetStringProperty("code"));

            await session.SendCommandAsync(0, "createStream", 2, cancellationToken, Amf0Value.Null);
            await session.ReadNextCommandAsync(cancellationToken);

            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task PlayAsync(string streamName, CancellationToken cancellationToken)
    {
        await SendCommandAsync(
            1,
            "play",
            3,
            cancellationToken,
            Amf0Value.Null,
            Amf0Value.String(streamName),
            Amf0Value.Number(-2));
    }

    public async Task<RtmpMessage?> ReadMessageAsync(CancellationToken cancellationToken) =>
        await _reader.ReadMessageAsync(_stream, cancellationToken);

    /// <summary>读取下一条 AMF0 命令（跳过控制消息），返回值已解码。</summary>
    public async Task<List<Amf0Value>> ReadNextCommandAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReadMessageAsync(cancellationToken);
            Assert.NotNull(message);

            if (message!.Type != RtmpMessageType.CommandAmf0)
            {
                continue;
            }

            Assert.True(Amf0.TryDecode(message.Payload, out var values, out var error), error);
            if (values.Count == 0 || values[0] is not Amf0String)
            {
                continue;
            }

            return values;
        }
    }

    /// <summary>读取下一条 onStatus 的 code/level。</summary>
    public async Task<(string Code, string? Level)> ReadNextStatusAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var values = await ReadNextCommandAsync(cancellationToken);
            if (values[0] is not Amf0String { Value: "onStatus" } || values.Count < 4)
            {
                continue;
            }

            var code = values[3].GetStringProperty("code");
            if (code is null)
            {
                continue;
            }

            return (code, values[3].GetStringProperty("level"));
        }
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
}
