namespace ZlmSharp.Tests;

/// <summary>ffmpeg/libx264 生成的 720p SPS/PPS，供 H.264/FLV 测试复用。</summary>
internal static class TestData
{
    public static readonly byte[] Sps720 =
    [
        0x67, 0xF4, 0x00, 0x1F, 0x91, 0x96, 0x40, 0x14, 0x01, 0x6E, 0xC0, 0x44,
        0x00, 0x00, 0x03, 0x00, 0x04, 0x00, 0x00, 0x03, 0x00, 0xC8, 0x3C, 0x60, 0xC9, 0x20,
    ];

    public static readonly byte[] Pps = [0x68, 0xEB, 0xC3, 0xC4, 0x48, 0x44];

    /// <summary>Annex-B 配置帧：SPS + PPS。</summary>
    public static byte[] AnnexBConfig()
    {
        var result = new byte[4 + Sps720.Length + 4 + Pps.Length];
        result[3] = 1;
        Sps720.CopyTo(result, 4);
        result[4 + Sps720.Length + 3] = 1;
        Pps.CopyTo(result, 4 + Sps720.Length + 4);
        return result;
    }

    public static byte[] AnnexBKeyFrame(byte fill = 0x11)
    {
        byte[] nal = [0x65, 0x88, 0x84, 0x00, fill, 0x21];
        var result = new byte[4 + nal.Length];
        result[3] = 1;
        nal.CopyTo(result, 4);
        return result;
    }

    public static byte[] AnnexBInterFrame(byte fill = 0x22)
    {
        byte[] nal = [0x41, 0x9A, 0x02, fill];
        var result = new byte[4 + nal.Length];
        result[3] = 1;
        nal.CopyTo(result, 4);
        return result;
    }
}
