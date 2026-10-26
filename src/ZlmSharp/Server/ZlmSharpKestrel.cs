using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ZlmSharp.Configuration;

namespace ZlmSharp.Server;

/// <summary>
/// 独立运行模式下的 Kestrel 装配（监听地址来自 ini）。
/// 嵌入式场景由宿主应用自行决定监听端口，不调用这里。
/// </summary>
public static class ZlmSharpKestrel
{
    public static void Configure(WebApplicationBuilder builder, AppOptions options)
    {
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = options.Http.MaxUploadSize;

            if (options.Http.Port <= 0)
            {
                return;
            }

            var listenIp = options.General.ListenIp?.Trim();
            if (!string.IsNullOrEmpty(listenIp) &&
                listenIp is not "::" and not "0.0.0.0" and not "*" &&
                IPAddress.TryParse(listenIp, out var ip))
            {
                kestrel.Listen(ip, options.Http.Port);
                return;
            }

            // 默认监听所有地址（IPv6 双栈）
            kestrel.ListenAnyIP(options.Http.Port);
        });
    }
}
