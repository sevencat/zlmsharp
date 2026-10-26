using System.Buffers.Binary;

namespace ZlmSharp.Protocols.Rtmp;

/// <summary>
/// RTMP chunk 写端：消息一律用 fmt=0 头 + fmt=3 续块，简单可靠。
/// 每条消息输出为独立字节数组，便于单测与直接写 socket。
/// </summary>
public sealed class RtmpChunkWriter
{
    private int _chunkSize = RtmpConstants.DefaultChunkSize;

    public int ChunkSize => _chunkSize;

    /// <summary>构造 Set Chunk Size 消息，并把后续输出块大小切换为新值。</summary>
    public byte[] BuildSetChunkSize(int size)
    {
        if (size is < 1 or > 0x7FFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        if (size == _chunkSize)
        {
            return [];
        }

        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)size);
        var message = BuildMessage(RtmpMessageType.SetChunkSize, 0, 0, payload, 2);
        _chunkSize = size;
        return message;
    }

    /// <summary>把一条消息切成若干 chunk 并拼接成待发送字节。</summary>
    public byte[] BuildMessage(
        byte type,
        uint streamId,
        uint timestamp,
        ReadOnlySpan<byte> payload,
        uint chunkStreamId = 3)
    {
        if (chunkStreamId is 0 or 1 || chunkStreamId > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkStreamId));
        }

        var chunkCount = payload.Length == 0 ? 1 : (payload.Length + _chunkSize - 1) / _chunkSize;
        var extended = timestamp >= 0xFFFFFF;
        var basicHeaderSize = chunkStreamId < 64 ? 1 : chunkStreamId < 320 ? 2 : 3;
        var continuationHeaderSize = basicHeaderSize + (extended ? 4 : 0);

        var buffer = new byte[basicHeaderSize + 11 + (extended ? 4 : 0) + payload.Length
            + (chunkCount - 1) * continuationHeaderSize];

        var offset = WriteBasicHeader(buffer, 0, 0, chunkStreamId);
        WriteUInt24(buffer, offset, extended ? 0xFFFFFFu : timestamp);
        offset += 3;
        WriteUInt24(buffer, offset, (uint)payload.Length);
        offset += 3;
        buffer[offset++] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), streamId);
        offset += 4;

        if (extended)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), timestamp);
            offset += 4;
        }

        var sent = 0;
        while (sent < payload.Length)
        {
            var size = Math.Min(_chunkSize, payload.Length - sent);
            payload.Slice(sent, size).CopyTo(buffer.AsSpan(offset));
            offset += size;
            sent += size;

            if (sent < payload.Length)
            {
                offset = WriteBasicHeader(buffer, offset, 3, chunkStreamId);
                if (extended)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), timestamp);
                    offset += 4;
                }
            }
        }

        return buffer;
    }

    private static int WriteBasicHeader(byte[] buffer, int offset, byte format, uint chunkStreamId)
    {
        if (chunkStreamId < 64)
        {
            buffer[offset] = (byte)((format << 6) | (byte)chunkStreamId);
            return offset + 1;
        }

        if (chunkStreamId < 320)
        {
            buffer[offset] = (byte)(format << 6);
            buffer[offset + 1] = (byte)(chunkStreamId - 64);
            return offset + 2;
        }

        var value = chunkStreamId - 64;
        buffer[offset] = (byte)((format << 6) | 1);
        buffer[offset + 1] = (byte)value;
        buffer[offset + 2] = (byte)(value >> 8);
        return offset + 3;
    }

    private static void WriteUInt24(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 16);
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)value;
    }
}
