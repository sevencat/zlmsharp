using System.Globalization;
using System.Text;

namespace ZlmSharp.Configuration;

/// <summary>ini 中的单个键值对（保留原始字符串，便于 API 原样返回）。</summary>
public sealed class IniEntry
{
    public IniEntry(string key, string value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    public string Value { get; set; }
}

/// <summary>ini 中的一个 [section]。</summary>
public sealed class IniSection
{
    private readonly List<IniEntry> _entries = new();
    private readonly Dictionary<string, IniEntry> _index = new(StringComparer.OrdinalIgnoreCase);

    public IniSection(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public IReadOnlyList<IniEntry> Entries => _entries;

    public string? Get(string key) => _index.TryGetValue(key, out var entry) ? entry.Value : null;

    public bool Contains(string key) => _index.ContainsKey(key);

    public void Set(string key, string value)
    {
        if (_index.TryGetValue(key, out var entry))
        {
            entry.Value = value;
            return;
        }

        var created = new IniEntry(key, value);
        _entries.Add(created);
        _index[key] = created;
    }
}

/// <summary>
/// 轻量 ini 文档：支持 <c>;</c>/<c>#</c> 整行注释、<c>[section]</c>、重复键后者生效。
/// 不使用 <c>IConfiguration</c> 反射绑定，NativeAOT 安全。
/// </summary>
public sealed class IniDocument
{
    private readonly List<IniSection> _sections = new();
    private readonly Dictionary<string, IniSection> _map = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IniSection> Sections => _sections;

    public IniSection GetOrAddSection(string name)
    {
        if (_map.TryGetValue(name, out var section))
        {
            return section;
        }

        section = new IniSection(name);
        _sections.Add(section);
        _map[name] = section;
        return section;
    }

    public IniSection? FindSection(string name) => _map.TryGetValue(name, out var section) ? section : null;

    public string? Get(string section, string key) => FindSection(section)?.Get(key);

    public string GetString(string section, string key, string fallback) => Get(section, key) ?? fallback;

    public int GetInt(string section, string key, int fallback) =>
        int.TryParse(Get(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public long GetLong(string section, string key, long fallback) =>
        long.TryParse(Get(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public double GetDouble(string section, string key, double fallback) =>
        double.TryParse(Get(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public bool GetBool(string section, string key, bool fallback) => ParseBool(Get(section, key), fallback);

    public void Set(string section, string key, string value) => GetOrAddSection(section).Set(key, value);

    /// <summary>用 <paramref name="other"/> 覆盖当前文档（值替换、新键追加、section 不存在则新建）。</summary>
    public void Overlay(IniDocument other)
    {
        foreach (var section in other._sections)
        {
            var target = GetOrAddSection(section.Name);
            foreach (var entry in section.Entries)
            {
                target.Set(entry.Key, entry.Value);
            }
        }
    }

    public string Render()
    {
        var builder = new StringBuilder();
        foreach (var section in _sections)
        {
            builder.Append('[').Append(section.Name).Append("]\n");
            foreach (var entry in section.Entries)
            {
                builder.Append(entry.Key).Append('=').Append(entry.Value).Append('\n');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    public IEnumerable<(string Section, string Key, string Value)> Flatten()
    {
        foreach (var section in _sections)
        {
            foreach (var entry in section.Entries)
            {
                yield return (section.Name, entry.Key, entry.Value);
            }
        }
    }

    public static IniDocument Parse(string text)
    {
        var document = new IniDocument();
        var current = document.GetOrAddSection(string.Empty);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t', '\uFEFF');
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                if (end > 1)
                {
                    current = document.GetOrAddSection(line[1..end].Trim());
                }

                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (key.Length > 0)
            {
                current.Set(key, value);
            }
        }

        // 没有 section 的键（含默认空 section）不参与业务配置，直接丢弃。
        document._sections.RemoveAll(s => s.Name.Length == 0);
        return document;
    }

    public static bool ParseBool(string? text, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return text.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }
}
