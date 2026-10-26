using System.Buffers.Binary;
using System.Text;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>RTSP 请求（RTSP/1.0，头部大小写不敏感）。</summary>
public sealed class RtspRequest
{
    public RtspRequest(
        string method,
        string uri,
        string version,
        IReadOnlyDictionary<string, string> headers,
        byte[] body)
    {
        Method = method;
        Uri = uri;
        Version = version;
        Headers = headers;
        Body = body;
    }

    public string Method { get; }

    public string Uri { get; }

    public string Version { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public byte[] Body { get; }

    public int CSeq => Header("CSeq") is { } value && int.TryParse(value, out var cseq) ? cseq : 0;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public bool MethodIs(string method) => string.Equals(Method, method, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Method} {Uri} cseq={CSeq}";
}

/// <summary>
/// RTSP 请求读取器：按行解析请求头 + Content-Length 读 body，
/// 同时跳过客户端发来的 TCP interleaved 帧（<c>$</c> 开头的 RTCP）。
/// </summary>
public sealed class RtspMessageReader
{
    private const int MaxLineLength = 8 * 1024;
    private const int MaxBodyLength = 1024 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[16 * 1024];

    private int _start;
    private int _end;

    public RtspMessageReader(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>读取一条请求；连接关闭返回 null。</summary>
    public async ValueTask<RtspRequest?> ReadRequestAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var first = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (first < 0)
            {
                return null;
            }

            if (first == '$')
            {
                var interleavedHeader = new byte[3];
                if (!await ReadExactAsync(interleavedHeader, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(interleavedHeader.AsSpan(1));
                if (length > 0 && !await SkipAsync(length, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                continue;
            }

            var requestLine = await ReadLineAsync(first, cancellationToken).ConfigureAwait(false);
            if (requestLine is null)
            {
                return null;
            }

            var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                throw new InvalidDataException($"RTSP 请求行非法: {requestLine}");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var line = await ReadLineAsync(-1, cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return null;
                }

                if (line.Length == 0)
                {
                    break;
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            var body = Array.Empty<byte>();
            if (headers.TryGetValue("Content-Length", out var lengthText) &&
                int.TryParse(lengthText, out var contentLength) &&
                contentLength > 0)
            {
                if (contentLength > MaxBodyLength)
                {
                    throw new InvalidDataException($"RTSP body 过大: {contentLength}");
                }

                body = new byte[contentLength];
                if (!await ReadExactAsync(body, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
            }

            return new RtspRequest(parts[0], parts[1], parts[2], headers, body);
        }
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_start == _end)
        {
            _end = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _start = 0;
            if (_end == 0)
            {
                return -1;
            }
        }

        return _buffer[_start++];
    }

    private async ValueTask<string?> ReadLineAsync(int firstByte, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(128);
        if (firstByte >= 0)
        {
            builder.Append((char)firstByte);
        }

        while (true)
        {
            var current = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
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
            if (builder.Length > MaxLineLength)
            {
                throw new InvalidDataException("RTSP 头部行过长");
            }
        }
    }

    private async ValueTask<bool> ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length && _start < _end)
        {
            buffer.Span[read++] = _buffer[_start++];
        }

        while (read < buffer.Length)
        {
            var count = await _stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private async ValueTask<bool> SkipAsync(int count, CancellationToken cancellationToken)
    {
        var scratch = new byte[Math.Min(count, 4096)];
        var remaining = count;

        while (remaining > 0)
        {
            var size = Math.Min(scratch.Length, remaining);
            if (!await ReadExactAsync(scratch.AsMemory(0, size), cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            remaining -= size;
        }

        return true;
    }
}

/// <summary>RTSP 响应拼装。</summary>
public static class RtspResponse
{
    public const string Version = "RTSP/1.0";

    public static byte[] Build(
        int statusCode,
        string reason,
        int cseq,
        IReadOnlyList<(string Name, string Value)>? headers = null,
        string? body = null)
    {
        var builder = new StringBuilder(256);
        builder.Append(Version).Append(' ').Append(statusCode).Append(' ').Append(reason).Append("\r\n");
        builder.Append("CSeq: ").Append(cseq).Append("\r\n");
        builder.Append("Server: zlmsharp\r\n");

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                builder.Append(name).Append(": ").Append(value).Append("\r\n");
            }
        }

        byte[] bodyBytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
        if (body is not null)
        {
            builder.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        }

        builder.Append("\r\n");

        var headerBytes = Encoding.UTF8.GetBytes(builder.ToString());
        if (bodyBytes.Length == 0)
        {
            return headerBytes;
        }

        var result = new byte[headerBytes.Length + bodyBytes.Length];
        headerBytes.CopyTo(result, 0);
        bodyBytes.CopyTo(result, headerBytes.Length);
        return result;
    }
}
