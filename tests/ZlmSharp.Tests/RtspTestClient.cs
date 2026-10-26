using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace ZlmSharp.Tests;

internal sealed record RtspTestResponse(
    int StatusCode,
    string Reason,
    IReadOnlyDictionary<string, string> Headers,
    string Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>测试用的最小 RTSP 客户端（发请求、读响应、读 TCP interleaved RTP）。</summary>
internal sealed class RtspTestClient : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly byte[] _buffer = new byte[16 * 1024];

    private int _start;
    private int _end;
    private int _cseq;

    private RtspTestClient(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _stream = stream;
    }

    public static async Task<RtspTestClient> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, cancellationToken);
        return new RtspTestClient(client, client.GetStream());
    }

    public Task SendAsync(
        string method,
        string uri,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] headers) =>
        SendAsync(method, uri, null, cancellationToken, headers);

    /// <summary>发送带 body 的请求（ANNOUNCE 的 SDP 用）。</summary>
    public Task SendAsync(
        string method,
        string uri,
        string? body,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] headers)
    {
        var builder = new StringBuilder(256);
        builder.Append(method).Append(' ').Append(uri).Append(" RTSP/1.0\r\n");
        builder.Append("CSeq: ").Append(++_cseq).Append("\r\n");
        builder.Append("User-Agent: zlmsharp-tests\r\n");

        foreach (var (name, value) in headers)
        {
            builder.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        var bodyBytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
        if (body is not null)
        {
            builder.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        }

        builder.Append("\r\n");

        var headerBytes = Encoding.UTF8.GetBytes(builder.ToString());
        var bytes = new byte[headerBytes.Length + bodyBytes.Length];
        headerBytes.CopyTo(bytes, 0);
        bodyBytes.CopyTo(bytes, headerBytes.Length);
        return WriteAsync(bytes, cancellationToken);
    }

    /// <summary>发送一帧 interleaved 数据（推流时用来发 RTP）。</summary>
    public async Task SendInterleavedAsync(int channel, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        header[0] = (byte)'$';
        header[1] = (byte)channel;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)payload.Length);

        var bytes = new byte[4 + payload.Length];
        header.CopyTo(bytes, 0);
        payload.CopyTo(bytes, 4);
        await WriteAsync(bytes, cancellationToken);
    }

    public async Task<RtspTestResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        var statusLine = await ReadLineAsync(cancellationToken);
        Assert.NotNull(statusLine);

        var parts = statusLine!.Split(' ', 3);
        Assert.Equal(3, parts.Length);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            Assert.NotNull(line);
            if (line!.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var body = string.Empty;
        if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length) && length > 0)
        {
            var bytes = new byte[length];
            await ReadExactAsync(bytes, cancellationToken);
            body = Encoding.UTF8.GetString(bytes);
        }

        return new RtspTestResponse(int.Parse(parts[1]), parts[2], headers, body);
    }

    /// <summary>读取一个 interleaved 帧，返回 RTP 包。</summary>
    public async Task<(int Channel, byte[] Payload)> ReadInterleavedAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactAsync(header, cancellationToken);

        Assert.Equal((byte)'$', header[0]);
        var channel = header[1];
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));

        var payload = new byte[length];
        await ReadExactAsync(payload, cancellationToken);
        return (channel, payload);
    }

    /// <summary>读取指定通道的 interleaved 帧，跳过其它通道（如 RTCP SR）。</summary>
    public async Task<byte[]> ReadInterleavedPayloadAsync(int channel, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (actual, payload) = await ReadInterleavedAsync(cancellationToken);
            if (actual == channel)
            {
                return payload;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(64);
        while (true)
        {
            var current = await ReadByteAsync(cancellationToken);
            if (current < 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            if (current == '\n')
            {
                if (builder.Length > 0 && builder[^1] == '\r')
                {
                    builder.Length--;
                }

                return builder.ToString();
            }

            builder.Append((char)current);
        }
    }

    private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_start == _end)
        {
            _end = await _stream.ReadAsync(_buffer, cancellationToken);
            _start = 0;
            if (_end == 0)
            {
                return -1;
            }
        }

        return _buffer[_start++];
    }

    private async Task ReadExactAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length && _start < _end)
        {
            destination.Span[read++] = _buffer[_start++];
        }

        while (read < destination.Length)
        {
            var count = await _stream.ReadAsync(destination[read..], cancellationToken);
            if (count == 0)
            {
                throw new IOException("连接已关闭");
            }

            read += count;
        }
    }
}
