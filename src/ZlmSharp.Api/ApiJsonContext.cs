using System.Text.Json.Serialization;

namespace ZlmSharp.Api;

/// <summary>API 序列化上下文（源生成，NativeAOT 下禁止反射序列化）。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApiVersionResponse))]
[JsonSerializable(typeof(ApiListResponse))]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(ApiMediaListResponse))]
[JsonSerializable(typeof(ApiCloseStreamsResponse))]
[JsonSerializable(typeof(ApiSessionListResponse))]
[JsonSerializable(typeof(ApiKickSessionResponse))]
[JsonSerializable(typeof(ApiYoloDetectResponse))]
[JsonSerializable(typeof(ApiYoloInfoResponse))]
[JsonSerializable(typeof(ApiYoloResultResponse))]
[JsonSerializable(typeof(ApiYoloOverlayResponse))]
[JsonSerializable(typeof(ApiYoloOverlayListResponse))]
[JsonSerializable(typeof(ApiYoloOverlayFlagResponse))]
[JsonSerializable(typeof(ApiRecordResponse))]
[JsonSerializable(typeof(ApiRecordListResponse))]
[JsonSerializable(typeof(ApiRecordFlagResponse))]
[JsonSerializable(typeof(ApiPushResponse))]
[JsonSerializable(typeof(ApiPushListResponse))]
[JsonSerializable(typeof(ApiPushFlagResponse))]
[JsonSerializable(typeof(ApiProxyKeyResponse))]
[JsonSerializable(typeof(ApiProxyFlagResponse))]
internal sealed partial class ApiJsonContext : JsonSerializerContext
{
}
