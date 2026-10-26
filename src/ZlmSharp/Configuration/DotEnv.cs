using System.Text;

namespace ZlmSharp.Configuration;

/// <summary>
/// <c>.env</c> 文档：KEY=VALUE、<c>#</c> 注释、<c>export</c> 前缀、单/双引号、
/// <c>${NAME}</c> 引用已解析变量或进程环境变量。
/// 与 zlmsharp.ini 的分工：ini 放业务配置（与 ZLM 对齐），.env 放机器相关/敏感/外部工具路径。
/// </summary>
public sealed class DotEnvDocument
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Values => _values;

    public string Get(string key, string fallback) => _values.TryGetValue(key, out var value) ? value : fallback;

    public bool TryGet(string key, out string value) => _values.TryGetValue(key, out value!);

    public void Set(string key, string value) => _values[key] = value;

    public static DotEnvDocument Parse(string text)
    {
        var document = new DotEnvDocument();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t', '\uFEFF');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line["export ".Length..].TrimStart();
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            var rawValue = line[(equals + 1)..].Trim();
            var expand = !(rawValue.Length >= 2 && rawValue[0] == '\'' && rawValue[^1] == '\'');
            var value = ParseValue(rawValue);
            document._values[key] = expand ? Expand(value, document) : value;
        }

        return document;
    }

    private static string ParseValue(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return Unescape(raw[1..^1]);
        }

        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1];
        }

        return raw;
    }

    private static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current != '\\' || index + 1 >= value.Length)
            {
                builder.Append(current);
                continue;
            }

            index++;
            builder.Append(value[index] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => value[index],
            });
        }

        return builder.ToString();
    }

    private static string Expand(string value, DotEnvDocument document)
    {
        if (value.IndexOf("${", StringComparison.Ordinal) < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '$' || index + 1 >= value.Length || value[index + 1] != '{')
            {
                builder.Append(value[index]);
                continue;
            }

            var end = value.IndexOf('}', index + 2);
            if (end < 0)
            {
                builder.Append(value[index]);
                continue;
            }

            var name = value[(index + 2)..end];
            var replacement = document.Get(name, Environment.GetEnvironmentVariable(name) ?? string.Empty);
            builder.Append(replacement);
            index = end;
        }

        return builder.ToString();
    }
}

public sealed class DotEnvLoadResult
{
    public required DotEnvDocument Document { get; init; }

    public required string EnvPath { get; init; }

    public required bool Created { get; init; }
}

/// <summary>加载 <c>.env</c>；缺省位于 exe 同目录，不存在时生成模板。进程环境变量优先于文件值。</summary>
public static class DotEnvLoader
{
    public const string FileName = ".env";

    public static string DefaultEnvPath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static DotEnvLoadResult Load(string? explicitPath)
    {
        var isExplicit = !string.IsNullOrWhiteSpace(explicitPath);
        var envPath = isExplicit ? Path.GetFullPath(explicitPath!.Trim()) : DefaultEnvPath;

        var created = false;
        if (!File.Exists(envPath))
        {
            if (isExplicit)
            {
                throw new FileNotFoundException($"指定的 .env 文件不存在: {envPath}", envPath);
            }

            var directory = Path.GetDirectoryName(envPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(envPath, DefaultEnv.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            created = true;
        }

        var document = DotEnvDocument.Parse(File.ReadAllText(envPath));

        // 让外部进程（ffmpeg）与后续代码都能通过标准环境变量读到；已存在的真实环境变量优先。
        foreach (var pair in document.Values)
        {
            if (Environment.GetEnvironmentVariable(pair.Key) is null)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
            else if (pair.Key.Length > 0)
            {
                document.Set(pair.Key, Environment.GetEnvironmentVariable(pair.Key)!);
            }
        }

        return new DotEnvLoadResult
        {
            Document = document,
            EnvPath = envPath,
            Created = created,
        };
    }
}

/// <summary>生成的 .env 模板（机器相关/敏感配置，不入库）。</summary>
public static class DefaultEnv
{
    public const string Text = """
        # zlmsharp 环境配置（.env）
        # 放机器相关、敏感、外部工具路径等不适合写进 zlmsharp.ini 的内容。
        # 优先级：进程环境变量 > .env > zlmsharp.ini 缺省值
        #
        # ffmpeg 可执行文件（留空则从 PATH 查找）
        # 本机可用：E:\program\ffmpeg\81\ffmpeg.exe
        ZLM_FFMPEG_BIN=
        # ffprobe 可执行文件（留空则从 PATH 查找）
        ZLM_FFPROBE_BIN=
        # 录像根目录（留空则用 zlmsharp.ini 中的 protocol.mp4_save_path）
        ZLM_RECORD_ROOT=
        # YOLO 检测（SimdYolo 纯托管推理）：模型路径（.pt/.onnx），留空则关闭
        # 本机模型仓库示例：F:\ai\models\yolo
        ZLM_YOLO_MODEL_DIR=
        # 只写文件名时到 ZLM_YOLO_MODEL_DIR 下找；相对路径按 exe 目录解析
        ZLM_YOLO_MODEL=
        # 预留：外部推理服务地址（当前走本地 SimdYolo，不使用）
        ZLM_YOLO_ENDPOINT=
        # 推理设备：cpu / vulkan / metal / auto
        ZLM_YOLO_DEVICE=cpu
        # 置信度阈值 0~1 / 模型输入尺寸（.pt 要求 32 的倍数）/ CPU 线程（0=默认）
        ZLM_YOLO_CONFIDENCE=0.25
        ZLM_YOLO_INPUT_WIDTH=640
        ZLM_YOLO_INPUT_HEIGHT=640
        ZLM_YOLO_CPU_THREADS=0
        # 同一路流两次检测的最小间隔（毫秒）；0 表示不限速
        ZLM_YOLO_SAMPLE_INTERVAL_MS=1000
        """;
}
