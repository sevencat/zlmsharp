using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace ZlmSharp.Protocols.Rtmp;

/// <summary>AMF0 编码/解码入口（RTMP 命令与 metadata）。</summary>
public static class Amf0
{
    private const byte MarkerNumber = 0x00;
    private const byte MarkerBoolean = 0x01;
    private const byte MarkerString = 0x02;
    private const byte MarkerObject = 0x03;
    private const byte MarkerNull = 0x05;
    private const byte MarkerUndefined = 0x06;
    private const byte MarkerEcmaArray = 0x08;
    private const byte MarkerObjectEnd = 0x09;
    private const byte MarkerStrictArray = 0x0A;
    private const byte MarkerDate = 0x0B;
    private const byte MarkerLongString = 0x0C;

    public static byte[] Encode(params Amf0Value[] values)
    {
        var writer = new ArrayBufferWriter<byte>(256);
        foreach (var value in values)
        {
            WriteValue(writer, value);
        }

        return writer.WrittenSpan.ToArray();
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out List<Amf0Value> values, out string error)
    {
        values = new List<Amf0Value>(4);
        var reader = new Amf0Reader(data);

        while (!reader.AtEnd)
        {
            if (!reader.TryReadValue(out var value) || value is null)
            {
                error = reader.Error.Length > 0 ? reader.Error : "AMF0 数据不完整";
                values.Clear();
                return false;
            }

            values.Add(value);
        }

        error = string.Empty;
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out List<Amf0Value> values) =>
        TryDecode(data, out values, out _);

    public static void WriteValue(IBufferWriter<byte> writer, Amf0Value value)
    {
        switch (value)
        {
            case Amf0Number number:
            {
                var span = writer.GetSpan(9);
                span[0] = MarkerNumber;
                BinaryPrimitives.WriteDoubleBigEndian(span[1..], number.Value);
                writer.Advance(9);
                break;
            }

            case Amf0Boolean boolean:
            {
                var span = writer.GetSpan(2);
                span[0] = MarkerBoolean;
                span[1] = boolean.Value ? (byte)1 : (byte)0;
                writer.Advance(2);
                break;
            }

            case Amf0String text:
                WriteString(writer, text.Value);
                break;

            case Amf0Object anonymous:
                WriteMarker(writer, MarkerObject);
                WriteProperties(writer, anonymous.Properties);
                WriteObjectEnd(writer);
                break;

            case Amf0EcmaArray ecma:
            {
                var span = writer.GetSpan(5);
                span[0] = MarkerEcmaArray;
                BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)ecma.Properties.Count);
                writer.Advance(5);
                WriteProperties(writer, ecma.Properties);
                WriteObjectEnd(writer);
                break;
            }

            case Amf0StrictArray array:
            {
                var span = writer.GetSpan(5);
                span[0] = MarkerStrictArray;
                BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)array.Items.Count);
                writer.Advance(5);
                foreach (var item in array.Items)
                {
                    WriteValue(writer, item);
                }

                break;
            }

            case Amf0Date date:
            {
                var span = writer.GetSpan(11);
                span[0] = MarkerDate;
                BinaryPrimitives.WriteDoubleBigEndian(span[1..], date.Milliseconds);
                span[9] = 0;
                span[10] = 0;
                writer.Advance(11);
                break;
            }

            case Amf0Null:
                WriteMarker(writer, MarkerNull);
                break;

            case Amf0Undefined:
                WriteMarker(writer, MarkerUndefined);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, "不支持的 AMF0 值类型");
        }
    }

    private static void WriteMarker(IBufferWriter<byte> writer, byte marker)
    {
        var span = writer.GetSpan(1);
        span[0] = marker;
        writer.Advance(1);
    }

    private static void WriteString(IBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);

        if (byteCount <= ushort.MaxValue)
        {
            var span = writer.GetSpan(3 + byteCount);
            span[0] = MarkerString;
            BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)byteCount);
            Encoding.UTF8.GetBytes(value, span[3..]);
            writer.Advance(3 + byteCount);
            return;
        }

        var longSpan = writer.GetSpan(5 + byteCount);
        longSpan[0] = MarkerLongString;
        BinaryPrimitives.WriteUInt32BigEndian(longSpan[1..], (uint)byteCount);
        Encoding.UTF8.GetBytes(value, longSpan[5..]);
        writer.Advance(5 + byteCount);
    }

    private static void WriteProperties(IBufferWriter<byte> writer, IReadOnlyDictionary<string, Amf0Value> properties)
    {
        foreach (var pair in properties)
        {
            var keyBytes = Encoding.UTF8.GetByteCount(pair.Key);
            var span = writer.GetSpan(2 + keyBytes);
            BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)keyBytes);
            Encoding.UTF8.GetBytes(pair.Key, span[2..]);
            writer.Advance(2 + keyBytes);

            WriteValue(writer, pair.Value);
        }
    }

    private static void WriteObjectEnd(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(3);
        span[0] = 0;
        span[1] = 0;
        span[2] = MarkerObjectEnd;
        writer.Advance(3);
    }
}

/// <summary>AMF0 顺序解码器（ref struct，避免额外分配）。</summary>
public ref struct Amf0Reader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _offset;

    public Amf0Reader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _offset = 0;
        Error = string.Empty;
    }

    public string Error { get; private set; }

    public readonly bool AtEnd => _offset >= _data.Length;

    public bool TryReadValue(out Amf0Value? value)
    {
        value = null;

        if (_offset >= _data.Length)
        {
            Error = "AMF0 数据已结束";
            return false;
        }

        var marker = _data[_offset++];
        switch (marker)
        {
            case 0x00:
            {
                if (!TrySlice(8, out var bytes))
                {
                    return false;
                }

                value = new Amf0Number(BinaryPrimitives.ReadDoubleBigEndian(bytes));
                return true;
            }

            case 0x01:
            {
                if (!TrySlice(1, out var bytes))
                {
                    return false;
                }

                value = new Amf0Boolean(bytes[0] != 0);
                return true;
            }

            case 0x02:
                return TryReadString(ushort.MaxValue, out value);

            case 0x0C:
                return TryReadString(int.MaxValue, out value);

            case 0x03:
                return TryReadProperties(isEcmaArray: false, out value);

            case 0x08:
            {
                if (!TrySlice(4, out _))
                {
                    return false;
                }

                return TryReadProperties(isEcmaArray: true, out value);
            }

            case 0x0A:
            {
                if (!TrySlice(4, out var countBytes))
                {
                    return false;
                }

                var count = BinaryPrimitives.ReadUInt32BigEndian(countBytes);
                var items = new List<Amf0Value>((int)Math.Min(count, 1024));
                for (var i = 0; i < count; i++)
                {
                    if (!TryReadValue(out var item) || item is null)
                    {
                        return false;
                    }

                    items.Add(item);
                }

                value = new Amf0StrictArray(items);
                return true;
            }

            case 0x0B:
            {
                if (!TrySlice(10, out var bytes))
                {
                    return false;
                }

                value = new Amf0Date(BinaryPrimitives.ReadDoubleBigEndian(bytes));
                return true;
            }

            case 0x05:
                value = Amf0Value.Null;
                return true;

            case 0x06:
                value = Amf0Value.Undefined;
                return true;

            default:
                Error = $"不支持的 AMF0 marker: 0x{marker:X2}";
                return false;
        }
    }

    private bool TryReadString(int maxLength, out Amf0Value? value)
    {
        value = null;
        var lengthSize = maxLength == ushort.MaxValue ? 2 : 4;

        if (!TrySlice(lengthSize, out var lengthBytes))
        {
            return false;
        }

        var length = lengthSize == 2
            ? BinaryPrimitives.ReadUInt16BigEndian(lengthBytes)
            : (int)BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);

        if (length < 0 || !TrySlice(length, out var bytes))
        {
            return false;
        }

        value = new Amf0String(Encoding.UTF8.GetString(bytes));
        return true;
    }

    private bool TryReadProperties(bool isEcmaArray, out Amf0Value? value)
    {
        value = null;
        var properties = new Dictionary<string, Amf0Value>(StringComparer.Ordinal);

        while (true)
        {
            if (!TrySlice(2, out var keyLengthBytes))
            {
                return false;
            }

            var keyLength = BinaryPrimitives.ReadUInt16BigEndian(keyLengthBytes);
            if (keyLength == 0)
            {
                if (!TrySlice(1, out var endMarker))
                {
                    return false;
                }

                if (endMarker[0] != 0x09)
                {
                    Error = $"AMF0 对象缺少结束标记（0x{endMarker[0]:X2}）";
                    return false;
                }

                break;
            }

            if (!TrySlice(keyLength, out var keyBytes))
            {
                return false;
            }

            if (!TryReadValue(out var propertyValue) || propertyValue is null)
            {
                return false;
            }

            properties[Encoding.UTF8.GetString(keyBytes)] = propertyValue;
        }

        value = isEcmaArray
            ? new Amf0EcmaArray(properties)
            : new Amf0Object(properties);
        return true;
    }

    private bool TrySlice(int length, out ReadOnlySpan<byte> slice)
    {
        if (length < 0 || _offset + length > _data.Length)
        {
            slice = default;
            Error = $"AMF0 数据越界（需要 {length} 字节，剩余 {_data.Length - _offset}）";
            return false;
        }

        slice = _data.Slice(_offset, length);
        _offset += length;
        return true;
    }
}
