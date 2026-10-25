using ZlmSharp.Configuration;

namespace ZlmSharp.Tests;

public class IniDocumentTests
{
    private const string Sample = """
        ; 分号注释
        # 井号注释
        [general]
        port = 8080
        port=9090
        name =  hello world
        [log]
        level=2
        file=
        """;

    [Fact]
    public void Parse_HandlesSectionsCommentsAndDuplicateKeys()
    {
        var document = IniDocument.Parse(Sample);

        Assert.Equal(2, document.Sections.Count);
        Assert.Equal(9090, document.GetInt("general", "port", 0));
        Assert.Equal("hello world", document.GetString("general", "name", ""));
        Assert.Equal(string.Empty, document.GetString("log", "file", "missing"));
        Assert.Null(document.Get("general", "not-exists"));
    }

    [Fact]
    public void Overlay_ReplacesValuesAndAppendsNewKeys()
    {
        var baseDocument = IniDocument.Parse("[a]\nx=1\ny=2");
        var overrides = IniDocument.Parse("[a]\ny=20\nz=30");

        baseDocument.Overlay(overrides);

        Assert.Equal("1", baseDocument.Get("a", "x"));
        Assert.Equal("20", baseDocument.Get("a", "y"));
        Assert.Equal("30", baseDocument.Get("a", "z"));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("bogus", false)]
    public void GetBool_ParsesCommonForms(string raw, bool expected)
    {
        var document = IniDocument.Parse($"[s]\nk={raw}");

        Assert.Equal(expected, document.GetBool("s", "k", fallback: false));
    }

    [Fact]
    public void Render_RoundTrips()
    {
        var document = IniDocument.Parse("[a]\nx=1");

        var rendered = document.Render();
        var reparsed = IniDocument.Parse(rendered);

        Assert.Equal("1", reparsed.Get("a", "x"));
    }
}

public class DotEnvTests
{
    [Fact]
    public void Parse_HandlesExportQuotesAndExpansion()
    {
        Environment.SetEnvironmentVariable("ZLM_TEST_ENV_VALUE", "proc");
        try
        {
            var document = DotEnvDocument.Parse("""
                # 注释
                export A=1
                B="x y"
                C='${A}z'
                D=${A}-${ZLM_TEST_ENV_VALUE}
                """);

            Assert.Equal("1", document.Get("A", ""));
            Assert.Equal("x y", document.Get("B", ""));
            Assert.Equal("${A}z", document.Get("C", ""));
            Assert.Equal("1-proc", document.Get("D", ""));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZLM_TEST_ENV_VALUE", null);
        }
    }

    [Fact]
    public void Parse_IgnoresMalformedLines()
    {
        var document = DotEnvDocument.Parse("not-a-pair\n=novalue\nX=1");

        Assert.Single(document.Values);
        Assert.Equal("1", document.Get("X", ""));
    }
}

public class ConfigSchemaTests
{
    [Theory]
    [InlineData("http", "port", "8080")]
    [InlineData("general", "enableVhost", "0")]
    [InlineData("http", "maxUploadSize", "1073741824")]
    public void Validate_AcceptsKnownKeys(string section, string key, string value)
    {
        Assert.True(ConfigSchema.TryValidate(section, key, value, out var error), error);
    }

    [Fact]
    public void Validate_RejectsUnknownKey()
    {
        Assert.False(ConfigSchema.TryValidate("http", "nope", "1", out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Validate_RejectsWrongType()
    {
        Assert.False(ConfigSchema.TryValidate("http", "port", "abc", out var error));
        Assert.NotEmpty(error);
    }
}

public class ConfigBinderTests
{
    [Fact]
    public void Bind_MapsDocumentToOptions()
    {
        var document = IniDocument.Parse(DefaultConfig.Text);
        document.Set("http", "port", "9090");
        document.Set("general", "enableVhost", "1");
        document.Set("log", "level", "1");

        var options = ConfigBinder.Bind(document);

        Assert.Equal(9090, options.Http.Port);
        Assert.True(options.General.EnableVhost);
        Assert.Equal(1, options.Log.Level);
        Assert.Empty(options.Warnings);
    }

    [Fact]
    public void Bind_InvalidPortFallsBackWithWarning()
    {
        var document = IniDocument.Parse(DefaultConfig.Text);
        document.Set("rtsp", "port", "70000");

        var options = ConfigBinder.Bind(document);

        Assert.Equal(554, options.Rtsp.Port);
        Assert.Single(options.Warnings);
    }

    [Fact]
    public void Bind_OutOfRangeLogLevelIsReset()
    {
        var document = IniDocument.Parse(DefaultConfig.Text);
        document.Set("log", "level", "99");

        var options = ConfigBinder.Bind(document);

        Assert.Equal(2, options.Log.Level);
        Assert.Single(options.Warnings);
    }
}

public class ConfigLoaderTests
{
    [Fact]
    public void Load_ReadsIniAndEnv()
    {
        var directory = Path.Combine(Path.GetTempPath(), "zlmsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var iniPath = Path.Combine(directory, "zlmsharp.ini");
        var envPath = Path.Combine(directory, ".env");
        File.WriteAllText(iniPath, DefaultConfig.Text.Replace("port=8080", "port=9091"));
        File.WriteAllText(envPath, "ZLM_FFMPEG_BIN=E:\\ffmpeg\\ffmpeg.exe");

        var previous = Environment.GetEnvironmentVariable(EnvOptions.FfmpegBinKey);
        try
        {
            var result = ConfigLoader.Load(iniPath, envPath);

            Assert.Equal(9091, result.Options.Http.Port);
            Assert.Equal(@"E:\ffmpeg\ffmpeg.exe", result.Options.Env.FfmpegBin);
            Assert.Equal(envPath, result.EnvPath);
            Assert.False(result.Created);
            Assert.False(result.EnvCreated);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvOptions.FfmpegBinKey, previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingExplicitConfigThrows()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "zlmsharp.ini");

        Assert.Throws<FileNotFoundException>(() => ConfigLoader.Load(missing));
    }
}
