using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using ZlmSharp.Configuration;

namespace ZlmSharp.Server;

/// <summary>
/// 管理页面（<c>/zlm/admin</c>）访问授权。是否启用、如何校验由宿主决定：
/// <para>
/// 默认实现是固定 token（<c>[api] pageToken</c>）：为空时全部放行；设置后要求
/// <c>?token=xxx</c>、请求头 <c>X-ZlmSharp-Token</c> 或此前校验通过写入的 cookie。
/// </para>
/// <para>
/// 需要对接 SSO、IP 白名单、动态口令等时，宿主注册自己的实现即可覆盖：
/// <c>services.AddSingleton&lt;IAdminPageAuthorizer, MyAuthorizer&gt;()</c>（在 <c>AddZlmSharp</c> 之后）。
/// </para>
/// </summary>
public interface IAdminPageAuthorizer
{
    /// <summary>返回 true 放行；false 由页面端点返回 401。</summary>
    ValueTask<bool> AuthorizeAsync(HttpContext context, CancellationToken cancellationToken);
}

/// <summary>固定 token 授权：token 可来自查询串、请求头，或上次成功访问写入的 cookie。</summary>
internal sealed class StaticTokenPageAuthorizer : IAdminPageAuthorizer
{
    /// <summary>校验通过后写入的 cookie 名（HttpOnly，Path 限定在管理页面）。</summary>
    internal const string CookieName = "zlm_admin_token";

    /// <summary>请求头里可替代查询参数的 token 字段。</summary>
    internal const string HeaderName = "X-ZlmSharp-Token";

    private readonly string? _token;

    public StaticTokenPageAuthorizer(AppOptions options) =>
        _token = string.IsNullOrEmpty(options.Api.PageToken) ? null : options.Api.PageToken;

    public ValueTask<bool> AuthorizeAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (_token is null)
        {
            return ValueTask.FromResult(true);
        }

        var fromQuery = context.Request.Query["token"].ToString();
        var fromHeader = context.Request.Headers[HeaderName].ToString();

        if (Matches(fromQuery) || Matches(fromHeader))
        {
            // 记住本次校验：浏览器刷新或直接打开页面时不必再带 token
            context.Response.Cookies.Append(
                CookieName,
                _token,
                new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/zlm/admin",
                    MaxAge = TimeSpan.FromDays(7),
                    IsEssential = true,
                });

            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(Matches(context.Request.Cookies[CookieName]));
    }

    private bool Matches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || _token is null)
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(_token);
        var actual = Encoding.UTF8.GetBytes(candidate);

        // 定长比较，避免通过响应时间逐字符猜 token
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
