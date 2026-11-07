using System.Buffers.Binary;
using System.Text;

namespace GlyphRaster;

/// <summary>
/// 最小实现的 TrueType（glyf 轮廓）字体解析器。
/// 只读 sfnt 目录、head、hhea、maxp、hmtx、cmap、loca、glyf、name 九个表。
/// </summary>
/// <remarks>
/// 支持 TTC 集合（取第一个字体）。CFF/OTF（OTTO）不支持，请用 .ttf 轮廓字体（例如 simhei.ttf）。
/// </remarks>
public sealed partial class TrueTypeFont
{
    private const uint TagTtcf = 0x74746366; // 'ttcf'
    private const uint TagOtto = 0x4F54544F; // 'OTTO'
    private const uint TagCmap = 0x636D6170;
    private const uint TagHead = 0x68656164;
    private const uint TagHhea = 0x68686561;
    private const uint TagHmtx = 0x686D7478;
    private const uint TagMaxp = 0x6D617870;
    private const uint TagLoca = 0x6C6F6361;
    private const uint TagGlyf = 0x676C7966;
    private const uint TagName = 0x6E616D65;

    private readonly byte[] _data;
    private readonly Dictionary<uint, TableRecord> _tables;

    private readonly int _head;
    private readonly int _hhea;
    private readonly int _hmtx;
    private readonly int _loca;
    private readonly int _glyf;
    private readonly int _indexToLocFormat;
    private readonly int _numberOfHMetrics;

    private int _cmapFormat = -1;
    private int _cmapOffset = -1;
    private int[] _endCodes = [];
    private int[] _startCodes = [];
    private short[] _idDeltas = [];
    private int[] _idRangeOffsets = [];
    private int _idRangeOffsetBase;
    private uint[] _groupStarts = [];
    private uint[] _groupEnds = [];
    private int[] _groupGlyphs = [];

    private readonly record struct TableRecord(int Offset, int Length);

    private TrueTypeFont(byte[] data)
    {
        _data = data;

        var directory = 0;
        if (data.Length >= 12 && ReadU32(0) == TagTtcf)
        {
            if (ReadU32(8) == 0)
            {
                throw new InvalidDataException("TTC 集合中没有字体。");
            }

            directory = checked((int)ReadU32(12));
        }

        if (data.Length < directory + 12)
        {
            throw new InvalidDataException("不是有效的 sfnt 字体文件。");
        }

        var sfntVersion = ReadU32(directory);
        if (sfntVersion == TagOtto)
        {
            throw new NotSupportedException("暂不支持 CFF/OTF 轮廓（OTTO），请使用 TrueType 轮廓字体（.ttf）。");
        }

        if (sfntVersion != 0x00010000 && sfntVersion != 0x74727565)
        {
            throw new InvalidDataException($"未知的 sfnt 版本 0x{sfntVersion:X8}。");
        }

        var numTables = ReadU16(directory + 4);
        _tables = new Dictionary<uint, TableRecord>(numTables);
        var record = directory + 12;
        for (var i = 0; i < numTables && record + 16 <= data.Length; i++, record += 16)
        {
            var offset = checked((int)ReadU32(record + 8));
            var length = checked((int)ReadU32(record + 12));
            if (offset >= 0 && offset < data.Length)
            {
                _tables[ReadU32(record)] = new TableRecord(offset, length);
            }
        }

        _head = RequireTable(TagHead, "head");
        _hhea = RequireTable(TagHhea, "hhea");
        _hmtx = RequireTable(TagHmtx, "hmtx");
        var maxp = RequireTable(TagMaxp, "maxp");
        var cmap = RequireTable(TagCmap, "cmap");
        _loca = RequireTable(TagLoca, "loca");
        _glyf = RequireTable(TagGlyf, "glyf");

        UnitsPerEm = ReadU16(_head + 18);
        if (UnitsPerEm <= 0)
        {
            UnitsPerEm = 1000;
        }

        _indexToLocFormat = ReadS16(_head + 50);
        Ascender = ReadS16(_hhea + 4);
        Descender = ReadS16(_hhea + 6);
        LineGap = ReadS16(_hhea + 8);
        _numberOfHMetrics = ReadU16(_hhea + 34);
        GlyphCount = ReadU16(maxp + 4);
        FamilyName = ReadFamilyName(OptionalTable(TagName));

        var (cmapOffset, cmapFormat) = PickCmapSubtable(cmap);
        if (cmapOffset >= 0)
        {
            _cmapFormat = cmapFormat;
            _cmapOffset = cmapOffset;
            if (cmapFormat == 4)
            {
                ParseCmapFormat4(cmapOffset);
            }
            else if (cmapFormat == 12)
            {
                ParseCmapFormat12(cmapOffset);
            }
        }
    }

    /// <summary>字体族名（name 表），取不到时为空字符串。</summary>
    public string FamilyName { get; }

    /// <summary>em 单位数（通常 1000 或 2048）。</summary>
    public int UnitsPerEm { get; }

    /// <summary>上伸部（字体单位）。</summary>
    public int Ascender { get; }

    /// <summary>下伸部（字体单位，通常为负）。</summary>
    public int Descender { get; }

    /// <summary>行间距（字体单位）。</summary>
    public int LineGap { get; }

    /// <summary>字形总数。</summary>
    public int GlyphCount { get; }

    public static TrueTypeFont Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new TrueTypeFont(data);
    }

    public static TrueTypeFont LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new TrueTypeFont(File.ReadAllBytes(path));
    }

    public static TrueTypeFont LoadFromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new TrueTypeFont(buffer.ToArray());
    }

    /// <summary>Unicode 码点 → 字形索引；0 表示缺字。</summary>
    public int GetGlyphIndex(int codePoint)
    {
        if (codePoint is < 0 or > 0x10FFFF)
        {
            return 0;
        }

        return _cmapFormat switch
        {
            0 => LookupFormat0(codePoint),
            4 => LookupFormat4(codePoint),
            6 => LookupFormat6(codePoint),
            12 => LookupFormat12(codePoint),
            _ => 0,
        };
    }

    public bool HasGlyph(int codePoint) => GetGlyphIndex(codePoint) != 0;

    /// <summary>字形前进宽度（字体单位）。</summary>
    public int GetAdvanceWidth(int glyphIndex)
    {
        if (_numberOfHMetrics <= 0 || glyphIndex < 0 || glyphIndex >= GlyphCount)
        {
            return 0;
        }

        return ReadU16(_hmtx + (Math.Min(glyphIndex, _numberOfHMetrics - 1) * 4));
    }

    // ---------- 表与基础读取 ----------

    private int RequireTable(uint tag, string name)
    {
        if (!_tables.TryGetValue(tag, out var table))
        {
            throw new InvalidDataException($"字体缺少 {name} 表。");
        }

        return table.Offset;
    }

    private int OptionalTable(uint tag) => _tables.TryGetValue(tag, out var table) ? table.Offset : -1;

    private ushort ReadU16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset, 2));

    private short ReadS16(int offset) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(offset, 2));

    private uint ReadU32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset, 4));

    private float ReadF2Dot14(int offset) => ReadS16(offset) / 16384f;

    // ---------- loca ----------

    private long LocaValue(int index)
    {
        var position = _indexToLocFormat == 0 ? _loca + (index * 2) : _loca + (index * 4);
        if (index < 0 || position + (_indexToLocFormat == 0 ? 2 : 4) > _data.Length)
        {
            return -1;
        }

        return _indexToLocFormat == 0 ? ReadU16(position) * 2L : ReadU32(position);
    }

    // ---------- cmap ----------

    private (int Offset, int Format) PickCmapSubtable(int cmap)
    {
        var count = ReadU16(cmap + 2);
        var bestOffset = -1;
        var bestScore = -1;
        var bestFormat = 0;
        for (var i = 0; i < count; i++)
        {
            var record = cmap + 4 + (i * 8);
            if (record + 8 > _data.Length)
            {
                break;
            }

            var offset = cmap + (int)ReadU32(record + 4);
            if (offset + 2 > _data.Length)
            {
                continue;
            }

            var format = ReadU16(offset);
            int score;
            switch (format)
            {
                case 12: score = 50; break;
                case 4: score = 40; break;
                case 6: score = 20; break;
                case 0: score = 10; break;
                default: continue;
            }

            score += (ReadU16(record), ReadU16(record + 2)) switch
            {
                (3, 10) => 100,
                (0, 6) => 95,
                (0, 4) => 94,
                (3, 1) => 90,
                (0, 3) => 85,
                (0, 2) => 80,
                (0, 1) => 75,
                (0, 0) => 70,
                (3, 0) => 20,
                _ => 5,
            };

            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
                bestFormat = format;
            }
        }

        return (bestOffset, bestFormat);
    }

    private void ParseCmapFormat4(int offset)
    {
        var segmentCount = ReadU16(offset + 6) / 2;
        if (segmentCount <= 0)
        {
            _cmapFormat = -1;
            return;
        }

        _endCodes = new int[segmentCount];
        _startCodes = new int[segmentCount];
        _idDeltas = new short[segmentCount];
        _idRangeOffsets = new int[segmentCount];

        var endBase = offset + 14;
        var startBase = endBase + (segmentCount * 2) + 2;
        var deltaBase = startBase + (segmentCount * 2);
        _idRangeOffsetBase = deltaBase + (segmentCount * 2);

        for (var i = 0; i < segmentCount; i++)
        {
            _endCodes[i] = ReadU16(endBase + (i * 2));
            _startCodes[i] = ReadU16(startBase + (i * 2));
            _idDeltas[i] = ReadS16(deltaBase + (i * 2));
            _idRangeOffsets[i] = ReadU16(_idRangeOffsetBase + (i * 2));
        }
    }

    private void ParseCmapFormat12(int offset)
    {
        var groupCount = (int)ReadU32(offset + 12);
        if (groupCount <= 0 || groupCount > 200000)
        {
            _cmapFormat = -1;
            return;
        }

        _groupStarts = new uint[groupCount];
        _groupEnds = new uint[groupCount];
        _groupGlyphs = new int[groupCount];
        var groupBase = offset + 16;
        for (var i = 0; i < groupCount; i++)
        {
            var p = groupBase + (i * 12);
            if (p + 12 > _data.Length)
            {
                break;
            }

            _groupStarts[i] = ReadU32(p);
            _groupEnds[i] = ReadU32(p + 4);
            _groupGlyphs[i] = (int)ReadU32(p + 8);
        }
    }

    private int LookupFormat0(int codePoint)
    {
        if (codePoint > 255 || _cmapOffset < 0)
        {
            return 0;
        }

        var position = _cmapOffset + 6 + codePoint;
        return position < _data.Length ? _data[position] : 0;
    }

    private int LookupFormat6(int codePoint)
    {
        if (_cmapOffset < 0)
        {
            return 0;
        }

        var first = _cmapOffset + 6;
        var firstCode = ReadU16(first);
        var entryCount = ReadU16(first + 2);
        if (codePoint < firstCode || codePoint >= firstCode + entryCount)
        {
            return 0;
        }

        return ReadU16(first + 4 + ((codePoint - firstCode) * 2));
    }

    private int LookupFormat4(int codePoint)
    {
        if (_endCodes.Length == 0)
        {
            return 0;
        }

        var lo = 0;
        var hi = _endCodes.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_endCodes[mid] < codePoint)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        if (_endCodes[lo] < codePoint || codePoint < _startCodes[lo])
        {
            return 0;
        }

        var rangeOffset = _idRangeOffsets[lo];
        var delta = _idDeltas[lo];
        if (rangeOffset == 0)
        {
            return (codePoint + delta) & 0xFFFF;
        }

        var position = _idRangeOffsetBase + (lo * 2) + rangeOffset + ((codePoint - _startCodes[lo]) * 2);
        if (position + 2 > _data.Length)
        {
            return 0;
        }

        var glyph = ReadU16(position);
        return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
    }

    private int LookupFormat12(int codePoint)
    {
        var lo = 0;
        var hi = _groupStarts.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var code = (uint)codePoint;
            if (code < _groupStarts[mid])
            {
                hi = mid - 1;
            }
            else if (code > _groupEnds[mid])
            {
                lo = mid + 1;
            }
            else
            {
                return _groupGlyphs[mid] + (codePoint - (int)_groupStarts[mid]);
            }
        }

        return 0;
    }

    // ---------- name ----------

    private string ReadFamilyName(int name)
    {
        if (name < 0)
        {
            return string.Empty;
        }

        try
        {
            var count = ReadU16(name + 2);
            var stringOffset = ReadU16(name + 4);
            for (var i = 0; i < count; i++)
            {
                var record = name + 6 + (i * 12);
                if (record + 12 > _data.Length)
                {
                    break;
                }

                var platform = ReadU16(record);
                var encoding = ReadU16(record + 2);
                var language = ReadU16(record + 4);
                var nameId = ReadU16(record + 6);
                if (nameId != 1 && nameId != 4)
                {
                    continue;
                }

                var isUnicode = platform == 3
                    ? (encoding == 1 || encoding == 10) && language is 0x409 or 0
                    : platform == 0;
                if (!isUnicode)
                {
                    continue;
                }

                var start = name + stringOffset + ReadU16(record + 10);
                var length = ReadU16(record + 8);
                if (start < 0 || start + length > _data.Length)
                {
                    continue;
                }

                var text = Encoding.BigEndianUnicode.GetString(_data, start, length);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }

        return string.Empty;
    }
}
