using System.Net;
using System.Net.Sockets;

namespace ZlmSharp.Protocols.Rtsp;

/// <summary>
/// RTSP UDP 传输：RTP / RTCP 各绑定一个随机本地端口，发往 SETUP 里客户端声明的端口。
/// 只发不收（客户端的 RTCP RR 直接丢弃，不影响推流）。
/// </summary>
internal sealed class UdpRtpTransport : IAsyncDisposable
{
    private readonly UdpClient _rtp;
    private readonly UdpClient _rtcp;
    private readonly IPEndPoint _remoteRtp;
    private readonly IPEndPoint _remoteRtcp;

    private UdpRtpTransport(UdpClient rtp, UdpClient rtcp, IPEndPoint remoteRtp, IPEndPoint remoteRtcp)
    {
        _rtp = rtp;
        _rtcp = rtcp;
        _remoteRtp = remoteRtp;
        _remoteRtcp = remoteRtcp;
    }

    public int RtpPort => ((IPEndPoint)_rtp.Client.LocalEndPoint!).Port;

    public int RtcpPort => ((IPEndPoint)_rtcp.Client.LocalEndPoint!).Port;

    /// <summary>在 <paramref name="remoteAddress"/> 同族地址上分配一对本地端口。</summary>
    public static UdpRtpTransport Create(IPAddress remoteAddress, int rtpPort, int rtcpPort)
    {
        // 监听是 dual-mode IPv6，IPv4 客户端拿到的是 ::ffff:a.b.c.d，必须还原成 IPv4 再发包
        if (remoteAddress.IsIPv4MappedToIPv6)
        {
            remoteAddress = remoteAddress.MapToIPv4();
        }

        var any = remoteAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Any
            : IPAddress.Any;

        var rtp = new UdpClient(new IPEndPoint(any, 0));
        UdpClient rtcp;
        try
        {
            rtcp = new UdpClient(new IPEndPoint(any, 0));
        }
        catch
        {
            rtp.Dispose();
            throw;
        }

        return new UdpRtpTransport(
            rtp,
            rtcp,
            new IPEndPoint(remoteAddress, rtpPort),
            new IPEndPoint(remoteAddress, rtcpPort));
    }

    public async ValueTask SendRtpAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        await _rtp.SendAsync(packet, _remoteRtp, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendRtcpAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        await _rtcp.SendAsync(packet, _remoteRtcp, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _rtp.Dispose();
        _rtcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
