using System.Buffers;
using ZlmSharp.Codecs;

namespace ZlmSharp.Tests;

public class AnnexBTests
{
    private static readonly byte[] Stream =
    [
        0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F,
        0, 0, 1, 0x68, 0xCE, 0x3C, 0x80,
        0, 0, 0, 1, 0x65, 0x88, 0x84, 0x21,
    ];

    [Fact]
    public void Split_HandlesThreeAndFourByteStartCodes()
    {
        var nalUnits = AnnexB.Split(Stream);

        Assert.Equal(3, nalUnits.Count);
        Assert.Equal(new byte[] { 0x67, 0x42, 0x00, 0x1F }, nalUnits[0]);
        Assert.Equal(new byte[] { 0x68, 0xCE, 0x3C, 0x80 }, nalUnits[1]);
        Assert.Equal(new byte[] { 0x65, 0x88, 0x84, 0x21 }, nalUnits[2]);
    }

    [Fact]
    public void ReadNext_DoesNotSwallowLeadingZeroOfFourByteCode()
    {
        // 4 字节起始码紧跟在 NALU 之后，不能把起始码的前导 0 算进 NALU
        byte[] data = [0, 0, 0, 1, 0x65, 0x80, 0, 0, 0, 1, 0x41, 0x01];

        var consumed = AnnexB.ReadNext(data, out var nal);

        Assert.Equal(new byte[] { 0x65, 0x80 }, nal.ToArray());
        Assert.Equal(6, consumed);
    }

    [Fact]
    public void Splitter_HandlesByteByByteInput()
    {
        var splitter = new AnnexBSplitter();
        var result = new List<byte[]>();

        foreach (var value in Stream)
        {
            splitter.Append([value]);
            while (splitter.TryRead(out var nal))
            {
                result.Add(nal!);
            }
        }

        result.AddRange(splitter.Flush());

        Assert.Equal(3, result.Count);
        Assert.Equal(new byte[] { 0x67, 0x42, 0x00, 0x1F }, result[0]);
        Assert.Equal(new byte[] { 0x65, 0x88, 0x84, 0x21 }, result[2]);
    }

    [Fact]
    public void Splitter_HandlesArbitraryChunks()
    {
        var splitter = new AnnexBSplitter();
        var result = new List<byte[]>();

        for (var offset = 0; offset < Stream.Length; offset += 5)
        {
            var chunk = Stream.AsSpan(offset, Math.Min(5, Stream.Length - offset));
            splitter.Append(chunk);
            result.AddRange(splitter.ReadAll());
        }

        result.AddRange(splitter.Flush());

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Splitter_IgnoresLeadingGarbage()
    {
        var splitter = new AnnexBSplitter();
        splitter.Append([0x12, 0x34, 0x56, 0, 0, 1, 0x67, 0x42]);

        Assert.True(splitter.TryRead(out _) == false); // 还没有下一个起始码
        splitter.Append([0, 0, 1, 0x68, 0xCE]);

        Assert.True(splitter.TryRead(out var first));
        Assert.Equal(new byte[] { 0x67, 0x42 }, first);
    }
}

public class H264SpsParserTests
{
    // ffmpeg (libx264) 生成的真实 SPS，含 emulation prevention 字节
    private static readonly byte[] Sps720 =
    [
        0x67, 0xF4, 0x00, 0x1F, 0x91, 0x96, 0x40, 0x14, 0x01, 0x6E, 0xC0, 0x44,
        0x00, 0x00, 0x03, 0x00, 0x04, 0x00, 0x00, 0x03, 0x00, 0xC8, 0x3C, 0x60, 0xC9, 0x20,
    ];

    private static readonly byte[] Sps1080 =
    [
        0x67, 0xF4, 0x00, 0x28, 0x91, 0x96, 0x40, 0x1E, 0x00, 0x89, 0xF8, 0x9C, 0x04, 0x40,
        0x00, 0x00, 0x03, 0x00, 0x40, 0x00, 0x00, 0x0C, 0x83, 0xC6, 0x0C, 0x92,
    ];

    [Fact]
    public void Parse_720p()
    {
        Assert.True(H264SpsParser.TryParse(Sps720, out var info, out var error), error);

        Assert.NotNull(info);
        Assert.Equal(1280, info!.Width);
        Assert.Equal(720, info.Height);
        Assert.Equal(244, info.ProfileIdc);
        Assert.Equal("High444", info.ProfileName);
        Assert.Equal(3, info.ChromaFormatIdc);
    }

    [Fact]
    public void Parse_1080p_AppliesFrameCropping()
    {
        Assert.True(H264SpsParser.TryParse(Sps1080, out var info, out var error), error);

        Assert.NotNull(info);
        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
    }

    [Fact]
    public void Parse_RejectsNonSps()
    {
        Assert.False(H264SpsParser.TryParse([0x68, 0xCE, 0x3C, 0x80], out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ToRbsp_RemovesEmulationPrevention()
    {
        var rbsp = H264SpsParser.ToRbsp([0x00, 0x00, 0x03, 0x01, 0x00, 0x00, 0x03, 0x02]);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x00, 0x02 }, rbsp);
    }
}

public class H264AvccTests
{
    private static readonly byte[] Sps =
    [
        0x67, 0xF4, 0x00, 0x1F, 0x91, 0x96, 0x40, 0x14, 0x01, 0x6E, 0xC0, 0x44,
        0x00, 0x00, 0x03, 0x00, 0x04, 0x00, 0x00, 0x03, 0x00, 0xC8, 0x3C, 0x60, 0xC9, 0x20,
    ];

    private static readonly byte[] Pps = [0x68, 0xEB, 0xC3, 0xC4, 0x48, 0x44];

    [Fact]
    public void ConfigRecord_RoundTrips()
    {
        var config = new H264Config { Sps = Sps, Pps = Pps };
        var record = config.BuildAvcConfigRecord();

        Assert.True(H264Avcc.TryParseConfigRecord(record, out var parsed, out var error), error);
        Assert.NotNull(parsed);
        Assert.Equal(Sps, parsed!.Sps);
        Assert.Equal(Pps, parsed.Pps);
        Assert.Equal(4, parsed.LengthSize);
        Assert.Equal(244, parsed.ProfileIdc);
    }

    [Fact]
    public void ToAnnexB_And_Back()
    {
        var nalUnits = new List<ReadOnlyMemory<byte>>
        {
            new(Sps),
            new(Pps),
            new byte[] { 0x65, 0x88, 0x84, 0x21 },
        };

        var avccWriter = new ArrayBufferWriter<byte>(64);
        var count = H264Avcc.ToAvcc(nalUnits, 4, avccWriter);
        Assert.Equal(3, count);

        var annexB = new ArrayBufferWriter<byte>(64);
        var annexCount = H264Avcc.ToAnnexB(avccWriter.WrittenSpan, 4, annexB);
        Assert.Equal(3, annexCount);

        var parsed = AnnexB.Split(annexB.WrittenSpan);
        Assert.Equal(3, parsed.Count);
        Assert.Equal(Sps, parsed[0]);
        Assert.Equal(new byte[] { 0x65, 0x88, 0x84, 0x21 }, parsed[2]);
    }

    [Fact]
    public void ContainsIdr_DetectsKeyFrame()
    {
        var writer = new ArrayBufferWriter<byte>(32);
        H264Avcc.ToAvcc(
            new ReadOnlyMemory<byte>[] { new byte[] { 0x41, 0x9A, 0x02 }, new byte[] { 0x65, 0x88 } },
            4,
            writer);

        Assert.True(H264Avcc.ContainsIdr(writer.WrittenSpan, 4));

        var nonKeyWriter = new ArrayBufferWriter<byte>(16);
        H264Avcc.ToAvcc(new ReadOnlyMemory<byte>[] { new byte[] { 0x41, 0x9A, 0x02 } }, 4, nonKeyWriter);
        Assert.False(H264Avcc.ContainsIdr(nonKeyWriter.WrittenSpan, 4));
    }

    [Fact]
    public void LengthPrefix_SupportsTwoBytes()
    {
        var writer = new ArrayBufferWriter<byte>(16);
        H264Avcc.ToAvcc(new ReadOnlyMemory<byte>[] { new byte[] { 0x65, 0x01, 0x02 } }, 2, writer);

        Assert.Equal(new byte[] { 0x00, 0x03, 0x65, 0x01, 0x02 }, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void AnnexB_KeyFrameDetection()
    {
        byte[] idr = [0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F, 0, 0, 0, 1, 0x65, 0x88];
        byte[] nonIdr = [0, 0, 1, 0x41, 0x9A, 0x02];

        Assert.True(H264NalUnit.ContainsIdrAnnexB(idr));
        Assert.False(H264NalUnit.ContainsIdrAnnexB(nonIdr));
    }
}
