namespace ZlmSharp.Cli;

/// <summary>命令行参数（与 ZLM 的 -c 保持一致，便于运维脚本迁移）。</summary>
internal sealed class CliArguments
{
    public string? ConfigPath { get; private set; }

    public string? EnvPath { get; private set; }

    public bool Daemon { get; private set; }

    public bool ShowHelp { get; private set; }

    public bool ShowVersion { get; private set; }

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        var result = new CliArguments();

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "-c":
                case "--conf":
                    if (index + 1 >= args.Count)
                    {
                        throw new ArgumentException($"{args[index]} 需要一个配置文件路径参数");
                    }

                    result.ConfigPath = args[++index];
                    break;

                case "-e":
                case "--env":
                    if (index + 1 >= args.Count)
                    {
                        throw new ArgumentException($"{args[index]} 需要一个 .env 文件路径参数");
                    }

                    result.EnvPath = args[++index];
                    break;

                case "-d":
                case "--daemon":
                    result.Daemon = true;
                    break;

                case "-v":
                case "--version":
                    result.ShowVersion = true;
                    break;

                case "-h":
                case "--help":
                case "-?":
                    result.ShowHelp = true;
                    break;

                default:
                    throw new ArgumentException($"未知参数: {args[index]}");
            }
        }

        return result;
    }
}
