using ZlmSharp.Protocols.Rtmp;

namespace ZlmSharp.Tests;

public class Amf0Tests
{
    [Fact]
    public void EncodeDecode_CommandWithObject()
    {
        var payload = Amf0.Encode(
            Amf0Value.String("connect"),
            Amf0Value.Number(1),
            Amf0Value.Object(
                ("app", Amf0Value.String("live")),
                ("tcUrl", Amf0Value.String("rtmp://127.0.0.1/live")),
                ("objectEncoding", Amf0Value.Number(0))));

        Assert.True(Amf0.TryDecode(payload, out var values, out var error), error);
        Assert.Equal(3, values.Count);

        var command = Assert.IsType<Amf0String>(values[0]);
        Assert.Equal("connect", command.Value);
        Assert.Equal(1, Assert.IsType<Amf0Number>(values[1]).Value);

        var properties = Assert.IsType<Amf0Object>(values[2]);
        Assert.Equal("live", properties.GetStringProperty("app"));
        Assert.Equal("rtmp://127.0.0.1/live", properties.GetStringProperty("tcUrl"));
        Assert.Equal(0, properties.GetNumberProperty("objectEncoding"));
        Assert.Equal(42, properties.GetNumberProperty("missing", 42));
    }

    [Fact]
    public void EncodeDecode_EcmaArrayAndStrictArray()
    {
        var payload = Amf0.Encode(
            Amf0Value.Number(1.5),
            Amf0Value.Boolean(true),
            Amf0Value.Null,
            Amf0Value.EcmaArray(
                ("width", Amf0Value.Number(1280)),
                ("encoder", Amf0Value.String("zlmsharp"))),
            Amf0Value.Array(Amf0Value.Number(1), Amf0Value.String("two")));

        Assert.True(Amf0.TryDecode(payload, out var values, out var error), error);
        Assert.Equal(5, values.Count);
        Assert.Equal(1.5, Assert.IsType<Amf0Number>(values[0]).Value);
        Assert.True(Assert.IsType<Amf0Boolean>(values[1]).Value);
        Assert.IsType<Amf0Null>(values[2]);

        var ecma = Assert.IsType<Amf0EcmaArray>(values[3]);
        Assert.Equal(1280, ecma.GetNumberProperty("width"));
        Assert.Equal("zlmsharp", ecma.GetStringProperty("encoder"));

        var array = Assert.IsType<Amf0StrictArray>(values[4]);
        Assert.Equal(2, array.Items.Count);
        Assert.Equal(1, Assert.IsType<Amf0Number>(array.Items[0]).Value);
        Assert.Equal("two", Assert.IsType<Amf0String>(array.Items[1]).Value);
    }

    [Fact]
    public void EncodeDecode_StringLengthBoundaries()
    {
        var shortText = new string('a', 10);
        var longText = new string('b', 70000);

        var payload = Amf0.Encode(Amf0Value.String(shortText), Amf0Value.String(longText));

        Assert.True(Amf0.TryDecode(payload, out var values, out var error), error);
        Assert.Equal(shortText, Assert.IsType<Amf0String>(values[0]).Value);
        Assert.Equal(longText, Assert.IsType<Amf0String>(values[1]).Value);
    }

    [Fact]
    public void Decode_TruncatedString_ReturnsFalse()
    {
        // marker=string, 声明长度 16，但只有 1 字节数据
        byte[] data = [0x02, 0x00, 0x10, 0x41];

        Assert.False(Amf0.TryDecode(data, out var values, out var error));
        Assert.NotEmpty(error);
        Assert.Empty(values);
    }

    [Fact]
    public void Decode_UnknownMarker_ReturnsFalse()
    {
        byte[] data = [0x0F];

        Assert.False(Amf0.TryDecode(data, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Decode_EmptyPayload_ReturnsEmptyList()
    {
        Assert.True(Amf0.TryDecode([], out var values, out var error), error);
        Assert.Empty(values);
    }
}
