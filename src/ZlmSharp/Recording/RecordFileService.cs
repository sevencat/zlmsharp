using ZlmSharp.Configuration;

namespace ZlmSharp.Recording;

/// <summary>
/// 录像文件浏览/清理（<c>getMp4RecordFile</c> / <c>deleteRecordFile</c>）。
/// 所有路径都限制在录像根目录内，避免目录穿越删除任意文件。
/// </summary>
public sealed class RecordFileService
{
    public RecordFileService(AppOptions options)
    {
        Root = RecordPaths.ResolveRoot(options);
    }

    public string Root { get; }

    /// <summary>
    /// 列出录像文件。
    /// <paramref name="period"/> 为空时返回该流目录下的日期子目录名；否则返回该日期目录下的文件名。
    /// <paramref name="path"/> 非空时按相对路径直接浏览（优先于 app/stream/period）。
    /// </summary>
    public IReadOnlyList<string> List(string app, string stream, string period, string? path = null)
    {
        var directory = ResolveBrowseDirectory(app, stream, period, path);
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        if (period.Length == 0 && string.IsNullOrWhiteSpace(path))
        {
            return Directory
                .GetDirectories(directory)
                .Select(item => Path.GetFileName(item))
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
        }

        return Directory
            .GetFiles(directory)
            .Select(item => Path.GetFileName(item))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>删除一个录像文件（相对录像根目录）。</summary>
    public bool Delete(string relativePath)
    {
        var full = ResolveSafePath(relativePath);
        if (full is null || !File.Exists(full))
        {
            return false;
        }

        File.Delete(full);
        return true;
    }

    /// <summary>删除指定日期目录下的一个文件。</summary>
    public bool Delete(string app, string stream, string period, string fileName) =>
        Delete(Path.Combine(
            RecordPaths.Sanitize(app),
            RecordPaths.Sanitize(stream),
            RecordPaths.Sanitize(period),
            Path.GetFileName(fileName)));

    private string? ResolveBrowseDirectory(string app, string stream, string period, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return ResolveSafePath(path);
        }

        if (app.Length == 0 || stream.Length == 0)
        {
            return Root;
        }

        var segments = new List<string> { RecordPaths.Sanitize(app), RecordPaths.Sanitize(stream) };
        if (period.Length > 0)
        {
            segments.Add(RecordPaths.Sanitize(period));
        }

        return ResolveSafePath(Path.Combine([.. segments]));
    }

    /// <summary>把相对路径解析成根目录内的绝对路径；越界返回 null。</summary>
    private string? ResolveSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var combined = Path.GetFullPath(Path.Combine(Root, relativePath));
        var rootWithSeparator = Root.EndsWith(Path.DirectorySeparatorChar)
            ? Root
            : Root + Path.DirectorySeparatorChar;

        return combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(combined, Root, StringComparison.OrdinalIgnoreCase)
            ? combined
            : null;
    }
}
