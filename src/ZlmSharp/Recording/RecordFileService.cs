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
    public IReadOnlyList<string> List(string app, string stream, string period, string? path = null) =>
        Browse(app, stream, period, path).Entries;

    /// <summary>
    /// 浏览录像目录，同时返回归一化的相对目录（<c>/</c> 分隔，不含根前缀），
    /// 供 API 给前端拼 <c>/record/{相对路径}</c> 访问地址。
    /// 目录下还有子目录（app / stream / 日期 / hls）时先列子目录，没有子目录才列文件，
    /// 前端按 <see cref="RecordBrowseResult.Directories"/> 区分渲染。
    /// </summary>
    public RecordBrowseResult Browse(string app, string stream, string period, string? path = null)
    {
        var directory = ResolveBrowseDirectory(app, stream, period, path);
        if (directory is null || !Directory.Exists(directory))
        {
            return new RecordBrowseResult(string.Empty, [], Directories: false);
        }

        var relative = ToRelativePath(directory) ?? string.Empty;
        var directories = Directory
            .GetDirectories(directory)
            .Select(item => Path.GetFileName(item))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        if (directories.Length > 0)
        {
            return new RecordBrowseResult(relative, directories, Directories: true);
        }

        var files = Directory
            .GetFiles(directory)
            .Select(item => Path.GetFileName(item))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        return new RecordBrowseResult(relative, files, Directories: false);
    }

    /// <summary>把绝对路径转成相对录像根目录的归一化路径（<c>/</c> 分隔）；不在根目录内返回 null。</summary>
    public string? ToRelativePath(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return null;
        }

        var full = Path.GetFullPath(absolutePath);
        var rootWithSeparator = Root.EndsWith(Path.DirectorySeparatorChar)
            ? Root
            : Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, Root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = Path.GetRelativePath(Root, full);
        return relative == "." ? string.Empty : relative.Replace('\\', '/');
    }

    /// <summary>把相对路径解析成根目录内的绝对文件路径；越界或不存在返回 null。</summary>
    public string? ResolveFile(string? relativePath) => ResolveSafePath(relativePath ?? string.Empty);

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

        // 只给 app 也能定位到该 app 目录；都没给就是录像根目录
        if (app.Length == 0)
        {
            return Root;
        }

        var segments = new List<string> { RecordPaths.Sanitize(app) };
        if (stream.Length > 0)
        {
            segments.Add(RecordPaths.Sanitize(stream));
        }

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
