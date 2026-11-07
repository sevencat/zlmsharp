using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ZlmSharp.Media.AutoGen;

/// <summary>
/// libav 运行环境：定位 ffmpeg 的 dll 目录、初始化网络、把 av_strerror 转成可读文本。
/// dll 与 ffmpeg.exe 放同一目录（avcodec-62 / avformat-62 / avutil-60 / swscale-9 / swresample-6）。
/// </summary>
internal static unsafe class LibavEnvironment
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static string _rootPath = string.Empty;

    /// <summary>dll 目录（未初始化前为空）。</summary>
    public static string RootPath => _rootPath;

    /// <summary>
    /// 初始化 libav：把 <paramref name="ffmpegPathOrDirectory"/> 解析成 dll 目录，设置
    /// <see cref="ffmpeg.RootPath"/> 并做一次网络初始化（RTSP/RTMP 需要）。重复调用只生效一次。
    /// </summary>
    /// <exception cref="InvalidOperationException">目录不存在，或 dll 加载失败。</exception>
    public static void Initialize(string? ffmpegPathOrDirectory)
    {
        var directory = ResolveDirectory(ffmpegPathOrDirectory);

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            ffmpeg.RootPath = directory;
            _rootPath = directory;

            try
            {
                ffmpeg.avformat_network_init();
                // 只把 error 级别打到 stderr，避免解码/编码过程中的 info 噪音
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                throw new InvalidOperationException(
                    $"加载 ffmpeg dll 失败（目录 {directory}）：{exception.Message}。" +
                    "请把 avcodec-62.dll / avformat-62.dll / avutil-60.dll / swscale-9.dll 等与 ffmpeg.exe 放在同一目录。",
                    exception);
            }

            _initialized = true;
        }
    }

    /// <summary>ffmpeg 版本串（初始化后可用）。</summary>
    public static string VersionInfo() => _initialized ? ffmpeg.av_version_info() : string.Empty;

    /// <summary>
    /// <c>ffmpeg.exe</c> 路径 → 所在目录；也可以直接传目录。空值回退到当前目录。
    /// </summary>
    public static string ResolveDirectory(string? ffmpegPathOrDirectory)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPathOrDirectory))
        {
            return AppContext.BaseDirectory;
        }

        var value = ffmpegPathOrDirectory.Trim().Trim('"');
        if (Directory.Exists(value))
        {
            return Path.GetFullPath(value);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(value));
        return string.IsNullOrEmpty(directory) ? AppContext.BaseDirectory : directory;
    }

    /// <summary>libav 错误码 → 文本。</summary>
    public static string ErrorText(int error)
    {
        if (error >= 0)
        {
            return $"code={error}";
        }

        var buffer = stackalloc byte[256];
        if (ffmpeg.av_strerror(error, buffer, 256) < 0)
        {
            return $"code={error}";
        }

        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"code={error}";
    }

    /// <summary>抛 libav 错误（带操作名与 URL）。</summary>
    public static InvalidOperationException Error(string operation, int error, string? url = null)
    {
        var text = ErrorText(error);
        return new InvalidOperationException(url is null
            ? $"{operation} 失败：{text}"
            : $"{operation} 失败（{url}）：{text}");
    }
}
