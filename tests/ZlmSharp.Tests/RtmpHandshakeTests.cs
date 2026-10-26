using System.Net;
using System.Net.Sockets;
using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Tests;

public class RtmpHandshakeTests
{
    [Fact]
    public async Task Server_UsesSimpleHandshake_S1VersionZeroAndS2EchoesC1()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            await RtmpHandshake.ServerAsync(accepted.GetStream(), CancellationToken.None);
        });

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();

            var c0c1 = new byte[1 + RtmpConstants.HandshakeSize];
            c0c1[0] = RtmpConstants.Version;
            Random.Shared.NextBytes(c0c1.AsSpan(1));
            await stream.WriteAsync(c0c1);
            await stream.FlushAsync();

            var s0s1 = new byte[1 + RtmpConstants.HandshakeSize];
            await ReadExactlyAsync(stream, s0s1);
            var s2 = new byte[RtmpConstants.HandshakeSize];
            await ReadExactlyAsync(stream, s2);

            Assert.Equal(RtmpConstants.Version, s0s1[0]);

            // S1 的 version 字段（S1 偏移 4~7，即 s0s1[5..9]）必须为 0，
            // 否则 ffmpeg 会当成复杂握手去校验 digest，报 "Server response validating failed"
            Assert.All(s0s1.AsSpan(5, 4).ToArray(), value => Assert.Equal(0, value));

            // S2 = C1 回显
            Assert.Equal(c0c1[1..], s2);

            // C2 = S1 回显，服务端读完握手结束
            await stream.WriteAsync(s0s1.AsSpan(1).ToArray());
            await stream.FlushAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..]);
            Assert.True(count > 0, "握手数据不完整");
            read += count;
        }
    }
}
