using System.Globalization;
using System.Text;

namespace ZlmSharp.Configuration;

public sealed class ConfigLoadResult
{
    public required AppOptions Options { get; init; }

    public required IniDocument Document { get; init; }

    public required DotEnvDocument DotEnv { get; init; }

    /// <summary>本次是否由程序自动生成了默认配置文件。</summary>
    public required bool Created { get; init; }

    public required bool EnvCreated { get; init; }

    public required string ConfigPath { get; init; }

    public required string EnvPath { get; init; }
}

/// <summary>
/// 配置加载：<c>zlmsharp.ini</c>（业务配置）+ <c>.env</c>（机器相关/敏感配置）。
/// 缺省都读 exe 同目录，不存在则写默认模板。
/// </summary>
public static class ConfigLoader
{
    public static string DefaultConfigPath => Path.Combine(AppContext.BaseDirectory, DefaultConfig.FileName);

    /// <param name="explicitPath">命令行 <c>-c</c> 指定的 ini 路径；为空则用默认路径。</param>
    /// <param name="explicitEnvPath">命令行 <c>-e</c> 指定的 .env 路径；为空则用默认路径。</param>
    public static ConfigLoadResult Load(string? explicitPath, string? explicitEnvPath = null)
    {
        var configPath = ResolvePath(explicitPath, DefaultConfigPath, DefaultConfig.Text, out var created);
        var env = DotEnvLoader.Load(explicitEnvPath);

        // 以默认配置为基底，保证升级后新增键有值，也避免用户文件缺键导致行为回退。
        var document = IniDocument.Parse(DefaultConfig.Text);
        document.Overlay(IniDocument.Parse(File.ReadAllText(configPath)));

        var options = ConfigBinder.Bind(document);
        options.ConfigPath = configPath;

        BindEnv(options.Env, env.Document);

        return new ConfigLoadResult
        {
            Options = options,
            Document = document,
            DotEnv = env.Document,
            Created = created,
            EnvCreated = env.Created,
            ConfigPath = configPath,
            EnvPath = env.EnvPath,
        };
    }

    private static string ResolvePath(string? explicitPath, string defaultPath, string defaultText, out bool created)
    {
        var isExplicit = !string.IsNullOrWhiteSpace(explicitPath);
        var path = isExplicit ? Path.GetFullPath(explicitPath!.Trim()) : defaultPath;

        created = false;
        if (!File.Exists(path))
        {
            if (isExplicit)
            {
                throw new FileNotFoundException($"指定的配置文件不存在: {path}", path);
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, defaultText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            created = true;
        }

        return path;
    }

    private static void BindEnv(EnvOptions env, DotEnvDocument document)
    {
        env.FfmpegBin = document.Get(EnvOptions.FfmpegBinKey, string.Empty);
        env.FfprobeBin = document.Get(EnvOptions.FfprobeBinKey, string.Empty);
        env.RecordRoot = document.Get(EnvOptions.RecordRootKey, string.Empty);
        env.YoloModelDir = document.Get(EnvOptions.YoloModelDirKey, string.Empty);
        env.YoloModel = document.Get(EnvOptions.YoloModelKey, string.Empty);
        env.YoloEndpoint = document.Get(EnvOptions.YoloEndpointKey, string.Empty);
        env.YoloDevice = document.Get(EnvOptions.YoloDeviceKey, env.YoloDevice);
        env.YoloConfidence = ParseFloat(document.Get(EnvOptions.YoloConfidenceKey, string.Empty), env.YoloConfidence);
        env.YoloInputWidth = ParseInt(document.Get(EnvOptions.YoloInputWidthKey, string.Empty), env.YoloInputWidth, 32, 8192);
        env.YoloInputHeight = ParseInt(document.Get(EnvOptions.YoloInputHeightKey, string.Empty), env.YoloInputHeight, 32, 8192);
        env.YoloCpuThreads = ParseInt(document.Get(EnvOptions.YoloCpuThreadsKey, string.Empty), env.YoloCpuThreads, 0, 256);
        env.YoloSampleIntervalMs = ParseInt(
            document.Get(EnvOptions.YoloSampleIntervalMsKey, string.Empty),
            env.YoloSampleIntervalMs,
            0,
            3_600_000);
        env.Raw = new Dictionary<string, string>(document.Values, StringComparer.OrdinalIgnoreCase);
    }

    private static float ParseFloat(string text, float fallback) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0f and <= 1f
            ? value
            : fallback;

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : fallback;
}
