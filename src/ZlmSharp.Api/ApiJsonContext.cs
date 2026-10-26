using System.Text.Json.Serialization;

namespace ZlmSharp.Api;

/// <summary>API 序列化上下文（源生成，NativeAOT 下禁止反射序列化）。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApiVersionResponse))]
[JsonSerializable(typeof(ApiListResponse))]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(ApiMediaListResponse))]
[JsonSerializable(typeof(ApiCloseStreamsResponse))]
[JsonSerializable(typeof(ApiYoloDetectResponse))]
[JsonSerializable(typeof(ApiYoloInfoResponse))]
internal sealed partial class ApiJsonContext : JsonSerializerContext
{
}
