using System.Net;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;

namespace ZlmSharp.Api;

/// <summary>
/// <c>/index/api/*</c> 端点。M0 只提供 version / getApiList，其余随里程碑逐步补齐。
/// 返回体与 ZLM 保持一致（<c>code</c>/<c>msg</c>/<c>data</c> 字段）。
/// </summary>
public static class ApiEndpoints
{
    private static readonly string[] ImplementedApis =
    [
        "/index/api/version",
        "/index/api/getApiList",
    ];

    public static void Map(IEndpointRouteBuilder endpoints, AppOptions options, ApiVersionInfo version)
    {
        endpoints.MapGet("/index/api/version", (RequestDelegate)(context => VersionAsync(context, options, version)));
        endpoints.MapGet("/index/api/getApiList", (RequestDelegate)(context => ApiListAsync(context, options)));
    }

    private static async Task VersionAsync(HttpContext context, AppOptions options, ApiVersionInfo version)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "version");
        var response = new ApiVersionResponse(
            Code: 0,
            Data: new ApiVersionData(version.BuildTime, version.BranchName, version.CommitHash));
        await WriteJsonAsync(context, response, ApiJsonContext.Default.ApiVersionResponse);
    }

    private static async Task ApiListAsync(HttpContext context, AppOptions options)
    {
        if (!await AuthorizeAsync(context, options))
        {
            return;
        }

        LogRequest(context, options, "getApiList");
        var response = new ApiListResponse(0, ImplementedApis);
        await WriteJsonAsync(context, response, ApiJsonContext.Default.ApiListResponse);
    }

    /// <summary>secret 校验：本机回环免校验（与 ZLM 行为一致），失败返回 401 + 错误体。</summary>
    private static async Task<bool> AuthorizeAsync(HttpContext context, AppOptions options)
    {
        var remote = Normalize(context.Connection.RemoteIpAddress);
        if (remote is not null && IPAddress.IsLoopback(remote))
        {
            return true;
        }

        if (string.IsNullOrEmpty(options.Api.Secret))
        {
            return true;
        }

        var provided = context.Request.Query["secret"].ToString();
        if (string.Equals(provided, options.Api.Secret, StringComparison.Ordinal))
        {
            return true;
        }

        var logger = GetLogger(context);
        logger?.LogWarning("API 鉴权失败: {RemoteIp} {Path}", remote, context.Request.Path);
        await WriteJsonAsync(context, new ApiErrorResponse(-1, "invalid secret"), ApiJsonContext.Default.ApiErrorResponse, StatusCodes.Status401Unauthorized);
        return false;
    }

    private static IPAddress? Normalize(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    private static void LogRequest(HttpContext context, AppOptions options, string api)
    {
        if (!options.Api.Debug)
        {
            return;
        }

        var logger = GetLogger(context);
        logger?.LogInformation(
            "API {Api} 来自 {RemoteIp}，查询参数: {Query}",
            api,
            context.Connection.RemoteIpAddress,
            context.Request.QueryString.Value);
    }

    private static ILogger? GetLogger(HttpContext context) =>
        context.RequestServices.GetService(typeof(ILoggerFactory)) is ILoggerFactory factory
            ? factory.CreateLogger("ZlmSharp.Api")
            : null;

    private static Task WriteJsonAsync<T>(HttpContext context, T value, JsonTypeInfo<T> typeInfo, int statusCode = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(value, typeInfo, cancellationToken: context.RequestAborted);
    }
}
