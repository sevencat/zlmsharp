using System.Buffers.Binary;

namespace ZlmSharp.Protocols.Rtmp;

/// <summary>
/// RTMP chunk 流解析：按 chunk stream id 重组消息，并处理 Chunk Size / Abort 控制消息。
/// 每个连接一个实例，单线程使用。
/// </summary>
public sealed class RtmpChunkReader
{
    private readonly Dictionary<uint, ChunkState> _states = [];
    private readonly byte[] _header = new byte[16];

    private int _chunkSize = RtmpConstants.DefaultChunkSize;

    /// <summary>当前入站 chunk 大小（对端 Set Chunk Size 生效后更新）。</summary>
    public int ChunkSize => _chunkSize;

    /// <summary>已读取的原始字节数，用于回 Acknowledgement。</summary>
    public long BytesRead { get; private set; }

    /// <summary>读取一条完整消息；连接关闭返回 null。</summary>
    public async ValueTask<RtmpMessage?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!await ReadExactAsync(stream, _header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var format = (byte)(_header[0] >> 6);
            var chunkStreamId = (uint)(_header[0] & 0x3F);

            if (chunkStreamId == 0)
            {
                if (!await ReadExactAsync(stream, _header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                chunkStreamId = 64u + _header[0];
            }
            else if (chunkStreamId == 1)
            {
                if (!await ReadExactAsync(stream, _header.AsMemory(0, 2), cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                chunkStreamId = 64u + _header[0] + ((uint)_header[1] << 8);
            }

            if (!_states.TryGetValue(chunkStreamId, out var state))
            {
                state = new ChunkState();
                _states[chunkStreamId] = state;
            }

            if (format == 3 && !state.HasHeader)
            {
                throw new InvalidDataException($"RTMP chunk 流 {chunkStreamId} 在缺少消息头时收到 fmt=3 分片");
            }

            uint timestamp;
            var hasExtendedTimestamp = false;

            switch (format)
            {
                case 0:
                {
                    if (!await ReadExactAsync(stream, _header.AsMemory(0, 11), cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }

                    var field = ReadUInt24(_header, 0);
                    var length = (int)ReadUInt24(_header, 3);
                    var type = _header[6];
                    var streamId = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(7, 4));

                    state.Reset(length, type, streamId);
                    timestamp = field;
                    hasExtendedTimestamp = field == 0xFFFFFF;
                    break;
                }

                case 1:
                {
                    if (!await ReadExactAsync(stream, _header.AsMemory(0, 7), cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }

                    var delta = ReadUInt24(_header, 0);
                    var length = (int)ReadUInt24(_header, 3);

                    state.Resize(length);
                    state.Type = _header[6];
                    timestamp = state.Timestamp + delta;
                    hasExtendedTimestamp = delta == 0xFFFFFF;
                    break;
                }

                case 2:
                {
                    if (!await ReadExactAsync(stream, _header.AsMemory(0, 3), cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }

                    var delta = ReadUInt24(_header, 0);
                    timestamp = state.Timestamp + delta;
                    hasExtendedTimestamp = delta == 0xFFFFFF;
                    break;
                }

                default:
                    timestamp = state.Timestamp;
                    hasExtendedTimestamp = state.HasExtendedTimestamp;
                    break;
            }

            if (hasExtendedTimestamp)
            {
                if (!await ReadExactAsync(stream, _header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                var extended = BinaryPrimitives.ReadUInt32BigEndian(_header);
                state.HasExtendedTimestamp = true;

                if (format == 0)
                {
                    timestamp = extended;
                }
                else if (format is 1 or 2)
                {
                    timestamp = state.Timestamp + extended;
                }
            }
            else if (format != 3)
            {
                state.HasExtendedTimestamp = false;
                state.Timestamp = timestamp;
            }

            state.Timestamp = timestamp;

            if (state.Length > RtmpConstants.MaxMessageSize)
            {
                throw new InvalidDataException($"RTMP 消息长度超限: {state.Length}");
            }

            var remaining = state.Length - state.Received;
            if (remaining > 0)
            {
                var toRead = Math.Min(_chunkSize, remaining);
                state.EnsureBuffer();
                if (!await ReadExactAsync(
                        stream,
                        state.Buffer.AsMemory(state.Received, toRead),
                        cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                state.Received += toRead;
            }

            if (state.Received < state.Length)
            {
                continue;
            }

            var message = new RtmpMessage(state.Type, state.StreamId, state.Timestamp, state.TakePayload());
            ApplyControlMessage(message);
            return message;
        }
    }

    private void ApplyControlMessage(RtmpMessage message)
    {
        switch (message.Type)
        {
            case RtmpMessageType.SetChunkSize when message.Payload.Length >= 4:
            {
                var size = (int)(BinaryPrimitives.ReadUInt32BigEndian(message.Payload) & 0x7FFFFFFF);
                if (size < 1)
                {
                    throw new InvalidDataException($"RTMP Set Chunk Size 非法: {size}");
                }

                _chunkSize = size;
                break;
            }

            case RtmpMessageType.Abort when message.Payload.Length >= 4:
            {
                var target = BinaryPrimitives.ReadUInt32BigEndian(message.Payload);
                if (_states.TryGetValue(target, out var state))
                {
                    state.Abort();
                }

                break;
            }
        }
    }

    private async ValueTask<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return false;
            }

            read += count;
            BytesRead += count;
        }

        return true;
    }

    private static uint ReadUInt24(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 16) | ((uint)buffer[offset + 1] << 8) | buffer[offset + 2];

    private sealed class ChunkState
    {
        public uint Timestamp;

        public int Length;

        public byte Type;

        public uint StreamId;

        public byte[] Buffer = [];

        public int Received;

        public bool HasHeader;

        public bool HasExtendedTimestamp;

        public void Reset(int length, byte type, uint streamId)
        {
            Length = length;
            Type = type;
            StreamId = streamId;
            Received = 0;
            HasHeader = true;
            Buffer = [];
        }

        public void Resize(int length)
        {
            if (Length != length)
            {
                Length = length;
                Received = 0;
                Buffer = [];
            }

            HasHeader = true;
        }

        public void EnsureBuffer()
        {
            if (Buffer.Length != Length)
            {
                Buffer = Length == 0 ? [] : new byte[Length];
                Received = Math.Min(Received, Length);
            }
        }

        public byte[] TakePayload()
        {
            var payload = Buffer;
            Buffer = [];
            Received = 0;
            return payload;
        }

        public void Abort()
        {
            Buffer = [];
            Received = 0;
            Length = 0;
            HasHeader = false;
        }
    }
}
