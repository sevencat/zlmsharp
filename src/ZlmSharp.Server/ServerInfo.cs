using System.Reflection;

namespace ZlmSharp.Server;

/// <summary>编译期写入的构建信息（供 <c>/index/api/version</c> 使用）。</summary>
public static class ServerInfo
{
    public static string Version { get; } = typeof(ServerInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string BuildTime { get; } = ReadMetadata("BuildTime");

    public static string BranchName { get; } = ReadMetadata("BranchName");

    public static string CommitHash { get; } = ReadMetadata("CommitHash");

    private static string ReadMetadata(string key)
    {
        foreach (var attribute in typeof(ServerInfo).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), inherit: false))
        {
            if (attribute is AssemblyMetadataAttribute metadata &&
                string.Equals(metadata.Key, key, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(metadata.Value))
            {
                return metadata.Value!;
            }
        }

        return "unknown";
    }
}
