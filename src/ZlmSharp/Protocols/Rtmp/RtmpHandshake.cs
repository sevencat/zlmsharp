namespace ZlmSharp.Protocols.Rtmp;

/// <summary>
/// RTMP 简单握手（C0/C1/C2 ↔ S0/S1/S2）。ZLM 默认同样使用简单握手，
/// 因为绝大多数推流端（OBS/ffmpeg）在服务端不回 digest 时也能继续。
/// </summary>
public static class RtmpHandshake
{
    private const int BlockSize = RtmpConstants.HandshakeSize;

    /// <summary>服务端握手：读 C0/C1C2，写 S0/S1/S2（S2 = C1 回显）。</summary>
    public static async Task ServerAsync(Stream stream, CancellationToken cancellationToken)
    {
        var version = new byte[1];
        await ReadExactlyAsync(stream, version, cancellationToken).ConfigureAwait(false);

        if (version[0] != RtmpConstants.Version)
        {
            throw new InvalidDataException($"不支持的 RTMP 版本: {version[0]}");
        }

        var c1 = new byte[BlockSize];
        await ReadExactlyAsync(stream, c1, cancellationToken).ConfigureAwait(false);

        // S1 = time(4) + zero(4) + random(1528)。版本字段必须为 0：
        // ffmpeg 看到 S1[5] >= 3 会当作复杂握手去校验 digest，简单握手必然失败
        // （现象就是 ffprobe 报 "Server response validating failed"）。
        var s1 = new byte[BlockSize];
        Random.Shared.NextBytes(s1);
        s1.AsSpan(0, 8).Clear();

        var output = new byte[1 + BlockSize + BlockSize];
        output[0] = RtmpConstants.Version;
        s1.CopyTo(output, 1);
        c1.CopyTo(output, 1 + BlockSize); // S2 = C1

        await stream.WriteAsync(output, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var c2 = new byte[BlockSize];
        await ReadExactlyAsync(stream, c2, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>客户端握手（测试与后续 RTMP 拉流复用）。</summary>
    public static async Task ClientAsync(Stream stream, CancellationToken cancellationToken)
    {
        var c0c1 = new byte[1 + BlockSize];
        c0c1[0] = RtmpConstants.Version;
        Random.Shared.NextBytes(c0c1.AsSpan(1));
        c0c1.AsSpan(5, 4).Clear(); // C1 版本号写 0：声明使用简单握手

        await stream.WriteAsync(c0c1, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var s0s1 = new byte[1 + BlockSize];
        await ReadExactlyAsync(stream, s0s1, cancellationToken).ConfigureAwait(false);

        if (s0s1[0] != RtmpConstants.Version)
        {
            throw new InvalidDataException($"服务端 RTMP 版本非法: {s0s1[0]}");
        }

        var s2 = new byte[BlockSize];
        await ReadExactlyAsync(stream, s2, cancellationToken).ConfigureAwait(false);

        var c2 = new byte[BlockSize];
        s0s1.AsSpan(1).CopyTo(c2); // C2 = S1
        await stream.WriteAsync(c2, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new InvalidDataException("RTMP 握手期间连接被关闭");
            }

            read += count;
        }
    }
}
