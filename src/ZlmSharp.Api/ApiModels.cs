namespace ZlmSharp.Api;

/// <summary>编译进程序的版本信息，对应 ZLM <c>/index/api/version</c> 的 data。</summary>
public sealed record ApiVersionInfo(string BuildTime, string BranchName, string CommitHash);

public sealed record ApiVersionData(string BuildTime, string BranchName, string CommitHash);

public sealed record ApiVersionResponse(int Code, ApiVersionData Data);

public sealed record ApiListResponse(int Code, IReadOnlyList<string> Data);

public sealed record ApiErrorResponse(int Code, string Msg);
