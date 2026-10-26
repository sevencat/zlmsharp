using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Tests;

public class RtmpChunkTests
{
    [Fact]
    public void BuildMessage_SplitsPayloadIntoChunks()
    {
        var writer = new RtmpChunkWriter();
        var payload = CreatePayload(300);

        var bytes = writer.BuildMessage(RtmpMessageType.Video, 1, 1234, payload);

        // 1(基本头) + 11(消息头) + 300 数据 + 2 个续块头(fmt=3)
        Assert.Equal(12 + payload.Length + 2, bytes.Length);
        Assert.Equal(0x00, bytes[0] >> 6); // fmt=0
        Assert.Equal(3, bytes[0] & 0x3F);  // csid=3
        Assert.Equal(0xC3, bytes[12 + 128]);
        Assert.Equal(0xC3, bytes[12 + 128 + 1 + 128]);
    }

    [Fact]
    public async Task Reader_ReassemblesMultiChunkMessage()
    {
        var writer = new RtmpChunkWriter();
        var payload = CreatePayload(300);
        var bytes = writer.BuildMessage(RtmpMessageType.Video, 7, 4321, payload);

        var reader = new RtmpChunkReader();
        using var stream = new MemoryStream(bytes);
        var message = await reader.ReadMessageAsync(stream, CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal(RtmpMessageType.Video, message!.Type);
        Assert.Equal(7u, message.StreamId);
        Assert.Equal(4321u, message.Timestamp);
        Assert.Equal(payload, message.Payload);
        Assert.Equal(bytes.Length, reader.BytesRead);
    }

    [Fact]
    public async Task Reader_AppliesSetChunkSizeBeforeFollowingMessage()
    {
        var writer = new RtmpChunkWriter();
        var setChunkSize = writer.BuildSetChunkSize(1024);
        var payload = CreatePayload(3000);
        var messageBytes = writer.BuildMessage(RtmpMessageType.Audio, 1, 9, payload);

        var reader = new RtmpChunkReader();
        Assert.Equal(128, reader.ChunkSize);

        using var stream = new MemoryStream([.. setChunkSize, .. messageBytes]);

        var control = await reader.ReadMessageAsync(stream, CancellationToken.None);
        Assert.Equal(RtmpMessageType.SetChunkSize, control!.Type);
        Assert.Equal(1024, reader.ChunkSize);

        var message = await reader.ReadMessageAsync(stream, CancellationToken.None);
        Assert.NotNull(message);
        Assert.Equal(payload, message!.Payload);
        // 3000 字节按 1024 分块 = 3 块（1 个头 + 2 个续块）
        Assert.Equal(12 + 3000 + 2, messageBytes.Length);
    }

    [Fact]
    public async Task ReaderAndWriter_HandleExtendedTimestamp()
    {
        var writer = new RtmpChunkWriter();
        const uint timestamp = 0x0100_0000; // > 0xFFFFFF，需要扩展时间戳
        var payload = CreatePayload(200);
        var bytes = writer.BuildMessage(RtmpMessageType.Video, 3, timestamp, payload);

        var reader = new RtmpChunkReader();
        using var stream = new MemoryStream(bytes);
        var message = await reader.ReadMessageAsync(stream, CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal(timestamp, message!.Timestamp);
        Assert.Equal(payload, message.Payload);
    }

    [Fact]
    public async Task Reader_ReturnsNullAtEndOfStream()
    {
        var reader = new RtmpChunkReader();
        using var stream = new MemoryStream();

        Assert.Null(await reader.ReadMessageAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void BuildSetChunkSize_UpdatesChunkSize()
    {
        var writer = new RtmpChunkWriter();
        var bytes = writer.BuildSetChunkSize(4096);

        Assert.NotEmpty(bytes);
        Assert.Equal(4096, writer.ChunkSize);
        Assert.Equal(0x02, bytes[0]); // fmt=0, csid=2
        Assert.Equal(RtmpMessageType.SetChunkSize, bytes[7]);
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
        {
            payload[i] = (byte)(i * 31 + 7);
        }

        return payload;
    }
}
