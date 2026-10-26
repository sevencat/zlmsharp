using System.Text;
using ZlmSharp.Protocols.Rtsp;

namespace ZlmSharp.Tests;

public class RtspMessageTests
{
    [Fact]
    public async Task ReadRequest_SkipsInterleavedFrameAndParsesHeadersAndBody()
    {
        var raw = new List<byte> { (byte)'$', 1, 0, 2, 0xAA, 0xBB };
        raw.AddRange(Encoding.ASCII.GetBytes(
            "DESCRIBE rtsp://127.0.0.1:8554/live/test RTSP/1.0\r\n" +
            "CSeq: 2\r\n" +
            "Accept: application/sdp\r\n" +
            "Content-Length: 3\r\n" +
            "\r\n" +
            "abc"));

        var reader = new RtspMessageReader(new MemoryStream(raw.ToArray()));
        var request = await reader.ReadRequestAsync(CancellationToken.None);

        Assert.NotNull(request);
        Assert.Equal("DESCRIBE", request!.Method);
        Assert.Equal("rtsp://127.0.0.1:8554/live/test", request.Uri);
        Assert.Equal("RTSP/1.0", request.Version);
        Assert.Equal(2, request.CSeq);
        Assert.Equal("application/sdp", request.Header("accept")); // 头部大小写不敏感
        Assert.Equal("abc", Encoding.ASCII.GetString(request.Body));
    }

    [Fact]
    public async Task ReadRequest_ReturnsNullAtEndOfStream()
    {
        var reader = new RtspMessageReader(new MemoryStream());
        Assert.Null(await reader.ReadRequestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadRequest_InvalidRequestLine_Throws()
    {
        var raw = Encoding.ASCII.GetBytes("GARBAGE\r\n\r\n");
        var reader = new RtspMessageReader(new MemoryStream(raw));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.ReadRequestAsync(CancellationToken.None));
    }

    [Fact]
    public void BuildResponse_WritesStatusCSeqAndContentLength()
    {
        var bytes = RtspResponse.Build(
            200,
            "OK",
            7,
            [("Content-Type", "application/sdp"), ("Session", "abc123")],
            "v=0\r\n");

        var text = Encoding.ASCII.GetString(bytes);

        Assert.StartsWith("RTSP/1.0 200 OK\r\n", text);
        Assert.Contains("CSeq: 7\r\n", text);
        Assert.Contains("Server: zlmsharp\r\n", text);
        Assert.Contains("Content-Type: application/sdp\r\n", text);
        Assert.Contains("Session: abc123\r\n", text);
        Assert.Contains("Content-Length: 5\r\n", text);
        Assert.EndsWith("\r\n\r\nv=0\r\n", text);
    }

    [Fact]
    public void BuildResponse_NoBodyOmitsContentLength()
    {
        var text = Encoding.ASCII.GetString(RtspResponse.Build(404, "Not Found", 3));

        Assert.Contains("CSeq: 3\r\n", text);
        Assert.DoesNotContain("Content-Length", text);
    }
}
