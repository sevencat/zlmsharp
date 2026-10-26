using ZlmSharp.Ai;
using ZlmSharp.Configuration;
using ZlmSharp.Core;

namespace ZlmSharp.Tests;

/// <summary>结果叠加：RGB24 绘制（纯托管位图字体）+ 叠加流参数校验（不真的起 ffmpeg）。</summary>
public class YoloOverlayTests
{
    [Fact]
    public void ColorOf_IsStableAndHandlesNegativeIds()
    {
        Assert.Equal(FrameOverlay.ColorOf(0), FrameOverlay.ColorOf(8));
        // 负值先去掉符号位再取模，结果稳定且不会越界
        Assert.Equal(FrameOverlay.ColorOf(7), FrameOverlay.ColorOf(-1));
        Assert.Equal(FrameOverlay.ColorOf(0), FrameOverlay.ColorOf(int.MinValue));
    }

    [Fact]
    public void FillRect_ClipsToImage()
    {
        var width = 4;
        var height = 3;
        var pixels = new byte[width * height * 3];

        FrameOverlay.FillRect(pixels, width, height, width * 3, -2, -1, 3, 3, (1, 2, 3));

        // 裁剪后只有 (0,0)、(0,1) 两个像素
        AssertPixel(pixels, width, 0, 0, (1, 2, 3));
        AssertPixel(pixels, width, 0, 1, (1, 2, 3));
        AssertPixel(pixels, width, 1, 0, (0, 0, 0));
        AssertPixel(pixels, width, 0, 2, (0, 0, 0));
    }

    [Fact]
    public void FillRect_OutOfBounds_DoesNothing()
    {
        var pixels = new byte[4 * 3 * 3];
        FrameOverlay.FillRect(pixels, 4, 3, 12, 10, 10, 5, 5, (9, 9, 9));
        Assert.All(pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public void FillRect_DoesNotTouchPaddingBytes()
    {
        var width = 2;
        var height = 2;
        var stride = (width * 3) + 4;
        var pixels = new byte[stride * height];

        FrameOverlay.FillRect(pixels, width, height, stride, 0, 0, width, height, (5, 6, 7));

        Assert.Equal(0, pixels[stride - 1]);
        Assert.Equal(0, pixels[(stride * 2) - 1]);
    }

    [Fact]
    public void DrawRect_PaintsBorderOnly()
    {
        var width = 20;
        var height = 12;
        var pixels = new byte[width * height * 3];
        var color = ((byte)10, (byte)20, (byte)30);

        FrameOverlay.DrawRect(pixels, width, height, width * 3, 2, 2, 10, 6, color, thickness: 2);

        AssertPixel(pixels, width, 2, 2, color);
        AssertPixel(pixels, width, 3, 2, color);
        AssertPixel(pixels, width, 2, 3, color);
        AssertPixel(pixels, width, 11, 2, color);
        AssertPixel(pixels, width, 2, 7, color);
        AssertPixel(pixels, width, 6, 4, (0, 0, 0));
        AssertPixel(pixels, width, 13, 2, (0, 0, 0));
    }

    [Fact]
    public void DrawRect_OutsideImage_DoesNotThrow()
    {
        var pixels = new byte[20 * 12 * 3];
        FrameOverlay.DrawRect(pixels, 20, 12, 60, -50, -50, 10, 10, (1, 1, 1));
        FrameOverlay.DrawRect(pixels, 20, 12, 60, 15, 8, 100, 100, (1, 1, 1));

        // 第二条框的顶边被裁到画面内（x 15..19，y=8）
        AssertPixel(pixels, 20, 19, 8, (1, 1, 1));
    }

    [Fact]
    public void DrawDetections_DrawsBoxAndLabel()
    {
        var width = 100;
        var height = 60;
        var pixels = new byte[width * height * 3];
        var objects = new List<YoloObject>
        {
            new(0, "PERSON", 0.93f, 10, 20, 30, 20),
        };

        FrameOverlay.DrawDetections(pixels, width, height, width * 3, objects);

        var color = FrameOverlay.ColorOf(0);
        AssertPixel(pixels, width, 10, 20, color);
        AssertPixel(pixels, width, 39, 20, color);
        AssertPixel(pixels, width, 20, 30, (0, 0, 0));

        // 标签从 y=20 向上放不下（标签高 18px），会落回框内，因此框内有白色字形
        Assert.True(CountWhite(pixels) > 30);
    }

    [Fact]
    public void DrawDetections_WithoutClassName_FallsBackToClassId()
    {
        var width = 120;
        var height = 40;
        var pixels = new byte[width * height * 3];

        FrameOverlay.DrawDetections(
            pixels,
            width,
            height,
            width * 3,
            [new YoloObject(7, string.Empty, 0.5f, 0, 0, 40, 30)]);

        Assert.True(CountWhite(pixels) > 10);
    }

    [Fact]
    public void DrawDetections_WithTrackId_StillDrawsLabel()
    {
        var pixels = new byte[160 * 40 * 3];

        FrameOverlay.DrawDetections(
            pixels,
            160,
            40,
            160 * 3,
            [new YoloObject(0, "PERSON", 0.9f, 10, 10, 50, 25) { TrackId = 12 }]);

        Assert.True(CountWhite(pixels) > 20);
    }

    [Fact]
    public void DrawDetections_NoObjects_LeavesFrameUntouched()
    {
        var pixels = new byte[32 * 16 * 3];
        FrameOverlay.DrawDetections(pixels, 32, 16, 96, []);
        Assert.All(pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public void DrawLabel_LongText_StaysInsideFrame()
    {
        var width = 24;
        var height = 20;
        var pixels = new byte[width * height * 3];

        FrameOverlay.DrawLabel(pixels, width, height, width * 3, 10, 0, "VERY-LONG-CLASS-NAME 100%", (1, 2, 3));

        // 画面最左、最右列都可能有像素，但不应越界（越界会抛异常）；顶部不能为负已由内部 clamp 保证
        Assert.Contains(pixels, value => value != 0);
    }

    [Fact]
    public void OverlayStreamNames_AreRecognized()
    {
        Assert.Equal("cam_yolo", YoloOverlayManager.OverlayStreamName("cam"));
        Assert.True(YoloOverlayManager.IsOverlayStream("cam_yolo"));
        Assert.True(YoloOverlayManager.IsOverlayStream("CAM_YOLO"));
        Assert.False(YoloOverlayManager.IsOverlayStream("yolo"));
        Assert.False(YoloOverlayManager.IsOverlayStream("cam"));
        Assert.False(YoloOverlayManager.IsOverlayStream(null));
    }

    [Fact]
    public void Start_OverlayStreamAgain_IsRejected()
    {
        using var manager = CreateManager(out _);

        var exception = Assert.Throws<ArgumentException>(() => manager.Start(string.Empty, "live", "cam_yolo"));
        Assert.Contains("不能对叠加流再叠加", exception.Message);
    }

    [Fact]
    public void Start_UnknownStream_IsRejected()
    {
        using var manager = CreateManager(out _);

        var exception = Assert.Throws<InvalidOperationException>(() => manager.Start(string.Empty, "live", "cam"));
        Assert.Contains("不在线", exception.Message);
    }

    [Fact]
    public void Start_WithoutResolution_IsRejected()
    {
        using var manager = CreateManager(out var registry);
        registry.GetOrCreate(StreamKey.Create(string.Empty, "live", "cam"), "rtmp");

        var exception = Assert.Throws<InvalidOperationException>(() => manager.Start(string.Empty, "live", "cam"));
        Assert.Contains("分辨率未知", exception.Message);
    }

    [Fact]
    public void Start_WithoutHttpOrRtmpPort_IsRejected()
    {
        using var noHttp = CreateManager(out var registry, httpPort: 0);
        registry.GetOrCreate(StreamKey.Create(string.Empty, "live", "cam"), "rtmp");
        Assert.Contains("HTTP", Assert.Throws<InvalidOperationException>(() => noHttp.Start(string.Empty, "live", "cam")).Message);

        using var noRtmp = CreateManager(out var rtmpRegistry, rtmpPort: 0);
        rtmpRegistry.GetOrCreate(StreamKey.Create(string.Empty, "live", "cam"), "rtmp");
        Assert.Contains("RTMP", Assert.Throws<InvalidOperationException>(() => noRtmp.Start(string.Empty, "live", "cam")).Message);
    }

    [Fact]
    public void StopAndList_EmptyManager_Behave()
    {
        using var manager = CreateManager(out _);

        Assert.False(manager.Stop(string.Empty, "live", "cam"));
        Assert.Empty(manager.List());
        Assert.Null(manager.Get(string.Empty, "live", "cam"));
        Assert.Equal(0, manager.StopAll());
    }

    private static YoloOverlayManager CreateManager(out MediaSourceRegistry registry, int httpPort = 8080, int rtmpPort = 1935)
    {
        var options = new AppOptions();
        options.Http.Port = httpPort;
        options.Rtmp.Port = rtmpPort;

        registry = new MediaSourceRegistry();
        return new YoloOverlayManager(registry, new YoloResultStore(), options);
    }

    private static void AssertPixel(byte[] pixels, int width, int x, int y, (byte R, byte G, byte B) expected)
    {
        var offset = ((y * width) + x) * 3;
        Assert.Equal(expected.R, pixels[offset]);
        Assert.Equal(expected.G, pixels[offset + 1]);
        Assert.Equal(expected.B, pixels[offset + 2]);
    }

    private static int CountWhite(byte[] pixels)
    {
        var count = 0;
        for (var index = 0; index + 2 < pixels.Length; index += 3)
        {
            if (pixels[index] == 255 && pixels[index + 1] == 255 && pixels[index + 2] == 255)
            {
                count++;
            }
        }

        return count;
    }
}
