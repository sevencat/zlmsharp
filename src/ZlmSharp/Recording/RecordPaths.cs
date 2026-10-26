using System.Globalization;
using ZlmSharp.Configuration;
using ZlmSharp.Core;

namespace ZlmSharp.Recording;

internal static class RecordPaths
{
    /// <summary>录像根目录：<c>ZLM_RECORD_ROOT</c> &gt; <c>protocol.mp4_save_path</c> &gt; <c>www</c>（相对路径按程序目录解析）。</summary>
    public static string ResolveRoot(AppOptions options)
    {
        var root = options.Env.RecordRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = options.Protocol.Mp4SavePath;
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = "www";
        }

        return Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(AppContext.BaseDirectory, root));
    }

    /// <summary>生成 <c>{root}/{app}/{stream}/{yyyy-MM-dd}/{HH-mm-ss}-{index}.{ext}</c> 并确保目录存在。</summary>
    public static string BuildPath(string root, StreamKey key, string extension, DateTimeOffset time, int index)
    {
        var directory = Path.Combine(
            root,
            Sanitize(key.App),
            Sanitize(key.Stream),
            time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(directory);

        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{time:HH-mm-ss}-{index:D3}.{extension}");

        return Path.Combine(directory, name);
    }

    /// <summary>HLS 目录：<c>{root}/{app}/{stream}/hls</c>（播放列表与切片都在这）。</summary>
    public static string BuildHlsDirectory(string root, StreamKey key) =>
        Path.Combine(root, Sanitize(key.App), Sanitize(key.Stream), "hls");

    internal static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = value.Trim().ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (Array.IndexOf(invalid, characters[index]) >= 0 || characters[index] == '.')
            {
                characters[index] = '_';
            }
        }

        var result = new string(characters).Trim('_');
        return result.Length == 0 ? "stream" : result;
    }
}
