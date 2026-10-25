using ZlmSharp.Core;

namespace ZlmSharp.Tests;

public class StreamUrlParserTests
{
    [Theory]
    [InlineData("/live/test.live.flv", "__defaultVhost__", "live", "test", "live", "flv")]
    [InlineData("live/test.live.flv", "__defaultVhost__", "live", "test", "live", "flv")]
    [InlineData("/v1/live/test.live.flv", "v1", "live", "test", "live", "flv")]
    [InlineData("http://127.0.0.1:8080/live/test.live.flv?secret=x#frag", "__defaultVhost__", "live", "test", "live", "flv")]
    [InlineData("/live/a.b.live.flv", "__defaultVhost__", "live", "a.b", "live", "flv")]
    [InlineData("/live/test.record.mp4", "__defaultVhost__", "live", "test", "record", "mp4")]
    public void TryParse_Valid(
        string input,
        string vhost,
        string app,
        string stream,
        string schema,
        string format)
    {
        Assert.True(StreamUrlParser.TryParse(input, out var url, out var error), error);

        Assert.Equal(vhost, url.Vhost);
        Assert.Equal(app, url.App);
        Assert.Equal(stream, url.Stream);
        Assert.Equal(schema, url.Schema);
        Assert.Equal(format, url.Format);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/live")]
    [InlineData("/live/test")]
    [InlineData("/live/test.flv")]
    [InlineData("/a/b/c/d.live.flv")]
    public void TryParse_Invalid(string input)
    {
        Assert.False(StreamUrlParser.TryParse(input, out _, out var error));
        Assert.NotEmpty(error);
    }
}
