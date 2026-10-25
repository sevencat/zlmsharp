using ZlmSharp.Configuration;

namespace ZlmSharp.Server;

/// <summary>
/// 嵌入宿主时的装配参数：既可以直接给 <see cref="AppOptions"/>（程序化配置），
/// 也可以只给配置文件路径，由 zlmsharp 自行加载 ini + .env。
/// </summary>
public sealed class ZlmSharpOptions
{
    /// <summary>zlmsharp.ini 路径；为空表示 exe 同目录（不存在则生成）。仅在 <see cref="Options"/> 为空时生效。</summary>
    public string? ConfigPath { get; set; }

    /// <summary>.env 路径；为空表示 exe 同目录（不存在则生成）。</summary>
    public string? EnvPath { get; set; }

    /// <summary>程序化配置；设置后不再读取 ini（除非同时指定 <see cref="ConfigPath"/> 且需要文件值）。</summary>
    public AppOptions? Options { get; set; }

    /// <summary>程序化 ini 文档（供 getServerConfig 使用），可为空。</summary>
    public IniDocument? Document { get; set; }

    /// <summary>程序化 .env 文档，可为空。</summary>
    public DotEnvDocument? DotEnv { get; set; }

    /// <summary>在加载结果上再改一次（嵌入式场景覆盖若干字段）。</summary>
    public Action<AppOptions>? ConfigureOptions { get; set; }

    /// <summary>是否由 zlmsharp 配置 NLog；嵌入到已有日志体系时可设为 false。</summary>
    public bool ConfigureLogging { get; set; } = true;

    /// <summary>配置 NLog 时是否清空宿主已有日志 provider（独立运行时应为 true）。</summary>
    public bool ClearOtherLoggingProviders { get; set; }

    /// <summary>是否注册 RTSP/RTMP 裸 TCP 监听（嵌入到自己已有端口管理时可关闭）。</summary>
    public bool RegisterStreamListeners { get; set; } = true;
}
