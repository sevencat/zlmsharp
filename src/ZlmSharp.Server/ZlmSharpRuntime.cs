using NLog.Config;
using ZlmSharp.Configuration;
using ZlmSharp.Server.Logging;

namespace ZlmSharp.Server;

/// <summary>
/// 运行期配置快照：ini + .env + 最终 AppOptions + NLog 配置。
/// 独立运行与嵌入宿主共用同一份，保证行为一致。
/// </summary>
public sealed class ZlmSharpRuntime
{
    private ZlmSharpRuntime(
        AppOptions options,
        IniDocument document,
        DotEnvDocument dotEnv,
        string? configPath,
        string? envPath,
        bool configCreated,
        bool envCreated,
        LoggingConfiguration? nlogConfiguration)
    {
        Options = options;
        Document = document;
        DotEnv = dotEnv;
        ConfigPath = configPath;
        EnvPath = envPath;
        ConfigCreated = configCreated;
        EnvCreated = envCreated;
        NLogConfiguration = nlogConfiguration;
    }

    public AppOptions Options { get; }

    public IniDocument Document { get; }

    public DotEnvDocument DotEnv { get; }

    public string? ConfigPath { get; }

    public string? EnvPath { get; }

    public bool ConfigCreated { get; }

    public bool EnvCreated { get; }

    internal LoggingConfiguration? NLogConfiguration { get; }

    public static ZlmSharpRuntime Create(ZlmSharpOptions settings)
    {
        AppOptions options;
        IniDocument document;
        DotEnvDocument dotEnv;
        string? configPath = settings.ConfigPath;
        string? envPath = settings.EnvPath;
        var configCreated = false;
        var envCreated = false;

        if (settings.Options is not null)
        {
            options = settings.Options;
            document = settings.Document ?? IniDocument.Parse(DefaultConfig.Text);
            dotEnv = settings.DotEnv ?? DotEnvDocument.Parse(string.Empty);
        }
        else
        {
            var loaded = ConfigLoader.Load(settings.ConfigPath, settings.EnvPath);
            options = loaded.Options;
            document = loaded.Document;
            dotEnv = loaded.DotEnv;
            configPath = loaded.ConfigPath;
            envPath = loaded.EnvPath;
            configCreated = loaded.Created;
            envCreated = loaded.EnvCreated;
        }

        settings.ConfigureOptions?.Invoke(options);

        var nlog = settings.ConfigureLogging ? NLogSetup.Create(options) : null;

        return new ZlmSharpRuntime(options, document, dotEnv, configPath, envPath, configCreated, envCreated, nlog);
    }
}
