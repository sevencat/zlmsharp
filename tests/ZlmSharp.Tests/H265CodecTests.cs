using ZlmSharp.Codecs;

namespace ZlmSharp.Tests;

/// <summary>HEVC 编解码基础：SPS 解析、NAL 头、hvcC 生成与回读。</summary>
public class H265CodecTests
{
    [Fact]
    public void SpsParser_ReadsResolutionAndProfile()
    {
        Assert.True(
            H265SpsParser.TryParse(TestData.H265Sps, out var info, out var error),
            error);

        Assert.NotNull(info);
        Assert.Equal(320, info!.Width);
        Assert.Equal(240, info.Height);
        Assert.Equal(1, info.ChromaFormatIdc);
        Assert.Equal(8, info.BitDepthLuma);
        Assert.Equal(8, info.BitDepthChroma);
        // 与 ffprobe 对拍：level=60、profile=Rext（x265 8bit 默认写 profile_idc=4）
        Assert.Equal(60, info.LevelIdc);
        Assert.Equal("Rext", info.ProfileName);
        Assert.Equal(4, info.ProfileIdc);
    }

    [Fact]
    public void SpsParser_RejectsNonSpsNal()
    {
        Assert.False(H265SpsParser.TryParse(TestData.H265Vps, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void NalHeader_IdentifiesParameterSetsAndKeyFrames()
    {
        Assert.True(H265NalUnit.TryReadHeader(TestData.H265Vps, out var vpsType, out _));
        Assert.Equal(H265NalUnitType.Vps, vpsType);
        Assert.True(H265NalUnit.IsParameterSet(vpsType));
        Assert.False(H265NalUnit.IsKeyFrame(vpsType));

        Assert.True(H265NalUnit.TryReadHeader(TestData.H265Sps, out var spsType, out _));
        Assert.Equal(H265NalUnitType.Sps, spsType);

        Assert.True(H265NalUnit.TryReadHeader(TestData.H265Pps, out var ppsType, out _));
        Assert.Equal(H265NalUnitType.Pps, ppsType);

        var keyFrame = TestData.AnnexBH265KeyFrame();
        var keyNal = AnnexB.Split(keyFrame)[0];
        Assert.True(H265NalUnit.TryReadHeader(keyNal, out var keyType, out _));
        Assert.Equal(H265NalUnitType.IdrWRadl, keyType);
        Assert.True(H265NalUnit.IsKeyFrame(keyType));
        Assert.True(H265NalUnit.IsVcl(keyType));

        var interNal = AnnexB.Split(TestData.AnnexBH265InterFrame())[0];
        Assert.True(H265NalUnit.TryReadHeader(interNal, out var interType, out _));
        Assert.Equal(H265NalUnitType.TrailR, interType);
        Assert.False(H265NalUnit.IsKeyFrame(interType));
    }

    [Fact]
    public void ConfigRecord_RoundTripsAllThreeParameterSets()
    {
        var config = new H265Config
        {
            Vps = TestData.H265Vps,
            Sps = TestData.H265Sps,
            Pps = TestData.H265Pps,
        };

        var record = config.BuildHvcConfigRecord();

        Assert.Equal(1, record[0]);          // configurationVersion
        Assert.Equal(3, record[22]);         // numOfArrays（VPS/SPS/PPS）
        Assert.Equal(0x03, record[21] & 0x03); // lengthSizeMinusOne = 3 → 4 字节

        Assert.True(H265Config.TryParseConfigRecord(record, out var parsed, out var error), error);
        Assert.NotNull(parsed);
        Assert.Equal(TestData.H265Vps, parsed!.Vps);
        Assert.Equal(TestData.H265Sps, parsed.Sps);
        Assert.Equal(TestData.H265Pps, parsed.Pps);
        Assert.Equal(4, parsed.LengthSize);
    }

    [Fact]
    public void AnnexBParameterSets_ContainThreeNals()
    {
        var config = new H265Config
        {
            Vps = TestData.H265Vps,
            Sps = TestData.H265Sps,
            Pps = TestData.H265Pps,
        };

        var nals = AnnexB.Split(config.BuildAnnexBParameterSets());

        Assert.Equal(3, nals.Count);
        Assert.Equal(H265NalUnitType.Vps, H265NalUnit.GetType(nals[0]));
        Assert.Equal(H265NalUnitType.Sps, H265NalUnit.GetType(nals[1]));
        Assert.Equal(H265NalUnitType.Pps, H265NalUnit.GetType(nals[2]));
    }
}
