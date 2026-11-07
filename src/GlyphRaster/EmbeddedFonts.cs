using System.IO.Compression;

namespace GlyphRaster;

/// <summary>编译进程序集的内置字体。</summary>
public enum BuiltInFont
{
    /// <summary>
    /// 文泉驿微米黑（WenQuanYi Micro Hei）：TTF 4.35 MB，gzip 后 2.01 MB。
    /// 覆盖 GB2312/GBK 常用汉字、假名与拉丁字母，与 OpenCV 5 打开 <c>WITH_UNIFONT</c> 时用的是同一个文件。
    /// </summary>
    WenQuanYiMicroHei,
}

/// <summary>
/// 内置字体数据：gzip 压缩后作为嵌入资源编进程序集，首次访问时解压并缓存。
/// 等价于 OpenCV 5 用 <c>ocv_blob2hdr</c> 把字体字节打进二进制、运行时 inflate 的做法，
/// 因此调用方不需要系统安装字体，也不需要任何外部文件。
/// </summary>
public static class EmbeddedFonts
{
    private const string WenQuanYiMicroHeiResource = "GlyphRaster.Fonts.WenQuanYiMicroHei.ttf.gz";

    private static readonly Lazy<byte[]> WenQuanYiMicroHeiData
        = new(static () => ReadResource(WenQuanYiMicroHeiResource));

    /// <summary>内置字体个数。</summary>
    public static int Count => 1;

    /// <summary>取内置字体的 TTF 字节（已解压）。返回共享缓存数组，调用方不要修改其内容。</summary>
    public static byte[] GetFontData(BuiltInFont font = BuiltInFont.WenQuanYiMicroHei) => font switch
    {
        BuiltInFont.WenQuanYiMicroHei => WenQuanYiMicroHeiData.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(font), font, "未知的内置字体。"),
    };

    /// <summary>解压后的 TTF 字节数。</summary>
    public static int GetFontSize(BuiltInFont font = BuiltInFont.WenQuanYiMicroHei)
        => GetFontData(font).Length;

    /// <summary>
    /// 用内置字体新建 <see cref="TextFont"/>（每次调用都会重新解析字体表）。
    /// 常规场景直接用 <see cref="TextFont.BuiltIn"/> 的共享实例即可。
    /// </summary>
    public static TextFont CreateFont(BuiltInFont font = BuiltInFont.WenQuanYiMicroHei)
        => TextFont.Load(GetFontData(font));

    private static byte[] ReadResource(string name)
    {
        var assembly = typeof(EmbeddedFonts).Assembly;
        using var raw = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"程序集缺少嵌入资源「{name}」。");
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var buffer = new MemoryStream(5 * 1024 * 1024);
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }
}
