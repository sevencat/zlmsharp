namespace ZlmSharp.Protocols.Rtmp;

/// <summary>AMF0 值：RTMP 命令（connect/publish…）与 onMetaData 共用。</summary>
public abstract record Amf0Value
{
    public static Amf0Value Null { get; } = new Amf0Null();

    public static Amf0Value Undefined { get; } = new Amf0Undefined();

    public static Amf0Value Number(double value) => new Amf0Number(value);

    public static Amf0Value String(string value) => new Amf0String(value);

    public static Amf0Value Boolean(bool value) => new Amf0Boolean(value);

    public static Amf0Value Object(params (string Key, Amf0Value Value)[] properties) =>
        new Amf0Object(ToMap(properties));

    public static Amf0Value EcmaArray(params (string Key, Amf0Value Value)[] properties) =>
        new Amf0EcmaArray(ToMap(properties));

    public static Amf0Value Array(params Amf0Value[] items) => new Amf0StrictArray(items);

    public static Amf0Value Date(double milliseconds) => new Amf0Date(milliseconds);

    private static Dictionary<string, Amf0Value> ToMap((string Key, Amf0Value Value)[] properties)
    {
        var map = new Dictionary<string, Amf0Value>(properties.Length, StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            map[key] = value;
        }

        return map;
    }
}

public sealed record Amf0Number(double Value) : Amf0Value;

public sealed record Amf0Boolean(bool Value) : Amf0Value;

public sealed record Amf0String(string Value) : Amf0Value;

/// <summary>匿名对象（marker 0x03）。</summary>
public sealed record Amf0Object(IReadOnlyDictionary<string, Amf0Value> Properties) : Amf0Value;

/// <summary>ECMA 数组（marker 0x08），onMetaData 常用。</summary>
public sealed record Amf0EcmaArray(IReadOnlyDictionary<string, Amf0Value> Properties) : Amf0Value;

/// <summary>严格数组（marker 0x0A）。</summary>
public sealed record Amf0StrictArray(IReadOnlyList<Amf0Value> Items) : Amf0Value;

public sealed record Amf0Date(double Milliseconds) : Amf0Value;

public sealed record Amf0Null : Amf0Value;

public sealed record Amf0Undefined : Amf0Value;

/// <summary>按属性名读取 AMF0 对象字段。</summary>
public static class Amf0ValueExtensions
{
    public static string? GetStringProperty(this Amf0Value value, string key) => value switch
    {
        Amf0Object o => ReadString(o.Properties, key),
        Amf0EcmaArray e => ReadString(e.Properties, key),
        _ => null,
    };

    public static double GetNumberProperty(this Amf0Value value, string key, double fallback = 0) => value switch
    {
        Amf0Object o => ReadNumber(o.Properties, key, fallback),
        Amf0EcmaArray e => ReadNumber(e.Properties, key, fallback),
        Amf0Number n => n.Value,
        _ => fallback,
    };

    private static string? ReadString(IReadOnlyDictionary<string, Amf0Value> properties, string key) =>
        properties.TryGetValue(key, out var value) && value is Amf0String s ? s.Value : null;

    private static double ReadNumber(IReadOnlyDictionary<string, Amf0Value> properties, string key, double fallback) =>
        properties.TryGetValue(key, out var value) && value is Amf0Number n ? n.Value : fallback;
}
