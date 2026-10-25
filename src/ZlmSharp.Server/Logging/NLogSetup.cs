using System.Text;
using NLog;
using NLog.Config;
using NLog.Targets;
using ZlmSharp.Configuration;
using NLogLogLevel = NLog.LogLevel;

namespace ZlmSharp.Server.Logging;

/// <summary>
/// NLog 程序化配置（不读 nlog.config，避免运行时反射加载类型，NativeAOT 友好）。
/// 目标：控制台 + 按大小滚动的文件；级别与路径来自 zlmsharp.ini 的 [log]。
/// </summary>
internal static class NLogSetup
{
    private const string Layout =
        "${longdate}|${level:uppercase=true:padding=-5}|${logger}|${message}${onexception:${newline}${exception:format=tostring}}";

    public static LoggingConfiguration Create(AppOptions options)
    {
        var configuration = new LoggingConfiguration();
        var minimum = ToLogLevel(options.Log.Level);

        if (minimum == NLogLogLevel.Off)
        {
            configuration.AddRuleForAllLevels(new NullTarget("null"));
            return configuration;
        }

        var targets = new List<Target>(2);

        if (options.Log.Console)
        {
            targets.Add(new ColoredConsoleTarget("console")
            {
                Layout = Layout,
                UseDefaultRowHighlightingRules = true,
                DetectConsoleAvailable = true,
                AutoFlush = true,
            });
        }

        if (!string.IsNullOrWhiteSpace(options.Log.File))
        {
            var path = options.Log.File;
            if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, path);
            }

            targets.Add(new FileTarget("file")
            {
                FileName = path,
                Layout = Layout,
                Encoding = Encoding.UTF8,
                CreateDirs = true,
                KeepFileOpen = true,
                AutoFlush = true,
                ArchiveAboveSize = Math.Max(1, options.Log.MaxFileSizeMB) * 1024L * 1024L,
                MaxArchiveFiles = Math.Max(1, options.Log.MaxFiles),
                ArchiveSuffixFormat = ".{#}",
            });
        }

        if (targets.Count == 0)
        {
            configuration.AddRuleForAllLevels(new NullTarget("null"));
            return configuration;
        }

        if (minimum >= NLogLogLevel.Info)
        {
            // 把框架自身的 trace~info 日志吞掉（每个媒体请求都会打，太吵）
            var swallow = new LoggingRule("Microsoft.*", NLogLogLevel.Trace, NLogLogLevel.Info, new NullTarget("nullMicrosoft"))
            {
                Final = true,
            };
            configuration.LoggingRules.Add(swallow);
        }

        foreach (var target in targets)
        {
            configuration.AddRule(minimum, NLogLogLevel.Fatal, target);
        }

        return configuration;
    }

    /// <summary>zlmsharp [log] level（0trace~6off）→ NLog 级别。</summary>
    public static NLogLogLevel ToLogLevel(int level) => level switch
    {
        0 => NLogLogLevel.Trace,
        1 => NLogLogLevel.Debug,
        2 => NLogLogLevel.Info,
        3 => NLogLogLevel.Warn,
        4 => NLogLogLevel.Error,
        5 => NLogLogLevel.Fatal,
        _ => NLogLogLevel.Off,
    };
}
