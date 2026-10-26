using ZlmSharp.Ai;
using ZlmSharp.Ai.Analysis;
using ZlmSharp.Core;

namespace ZlmSharp.Tests;

/// <summary>通用分析层：结果存储、YOLO 适配、可插拔 renderer、分析器注册表。</summary>
public class AnalysisLayerTests
{
    private static readonly StreamKey Key = StreamKey.Create(null, "live", "cam");

    [Fact]
    public void Store_KeepsLatestResultPerStreamAndAnalyzer()
    {
        var store = new AnalysisResultStore();

        store.Set(Result("yolo", primitives: 2, at: DateTimeOffset.UtcNow.AddSeconds(-5)));
        store.Set(Result("yolo", primitives: 3));
        store.Set(Result("ocr", primitives: 1));

        Assert.Equal(3, store.Get(Key, "yolo")!.Primitives.Count);
        Assert.Single(store.Get(Key, "ocr")!.Primitives);
        Assert.Single(store.List("", "", "cam", "ocr"));

        store.RemoveStream(Key);
        Assert.Empty(store.List("", "", "cam"));
    }

    [Fact]
    public void Store_FiltersByStreamAndAnalyzer()
    {
        var store = new AnalysisResultStore();
        store.Set(Result("yolo"));
        store.Set(new AnalysisResult(
            "yolo",
            StreamKey.Create(null, "live", "other"),
            DateTimeOffset.UtcNow,
            1,
            640,
            480,
            []));

        Assert.Single(store.List("", "", "cam"));
        Assert.Single(store.List("", "live", "other", "yolo"));
        Assert.Empty(store.List("", "", "cam", "ocr"));
        Assert.Equal(2, store.List("", "", "").Count);
    }

    [Fact]
    public void YoloSnapshot_MapsToGenericPrimitives()
    {
        var snapshot = new YoloSnapshot(
            "__defaultVhost__",
            "live",
            "cam",
            DateTimeOffset.UtcNow,
            12,
            320,
            240,
            [
                new YoloObject(2, "car", 0.91f, 10, 20, 30, 40) { TrackId = 7 },
                new YoloObject(0, "person", 0.75f, 50, 60, 20, 80),
            ],
            null);

        var result = YoloAnalyzer.FromSnapshot(snapshot);

        Assert.Equal("yolo", result.AnalyzerId);
        Assert.Equal("cam", result.Key.Stream);
        Assert.Equal(2, result.Primitives.Count);

        var car = result.Primitives[0];
        Assert.Equal(AnalysisKind.Box, car.Kind);
        Assert.Equal("car", car.Label);
        Assert.Equal(7, car.TrackId);
        Assert.Equal(10, car.X);
        Assert.Equal(40, car.Height);
        Assert.Equal("2", car.Attributes!["classId"]);

        Assert.Null(result.Primitives[1].TrackId);
    }

    [Fact]
    public void Composer_DrawsBoxesPolygonsAndTextWithoutThrowing()
    {
        var image = new RgbImage(new byte[64 * 64 * 3], 64, 64);
        var result = new AnalysisResult(
            "yolo",
            Key,
            DateTimeOffset.UtcNow,
            3,
            64,
            64,
            [
                new AnalysisPrimitive(AnalysisKind.Box, "car", 0.9, 1, 5, 5, 20, 10),
                new AnalysisPrimitive(
                    AnalysisKind.Polygon,
                    "zone",
                    0,
                    null,
                    Points: [new AnalysisPoint(2, 2), new AnalysisPoint(50, 4), new AnalysisPoint(30, 60)]),
                new AnalysisPrimitive(AnalysisKind.Text, "ocr", 0.8, null, 20, 30, Text: "ABC-123"),
                // 越界坐标不应抛异常
                new AnalysisPrimitive(AnalysisKind.Box, "edge", 0.5, null, -50, -50, 500, 500),
            ]);

        var composer = OverlayComposer.Default();
        composer.Render(image, result);

        Assert.Equal(3, composer.Renderers.Count);
        Assert.Contains(image.Pixels, value => value != 0);
    }

    [Fact]
    public void Registry_ListsEnabledAnalyzers()
    {
        var registry = new AnalyzerRegistry();
        registry.Register(new FakeAnalyzer("yolo", enabled: true));
        registry.Register(new FakeAnalyzer("ocr", enabled: false));

        Assert.Equal(2, registry.All.Count);
        Assert.Single(registry.Enabled);
        Assert.Equal("yolo", registry.Enabled[0].Id);
        Assert.Null(registry.Get("missing"));
    }

    [Fact]
    public async Task FakeAnalyzer_FlowsThroughStore()
    {
        var store = new AnalysisResultStore();
        var analyzer = new FakeAnalyzer("ocr", enabled: true);

        var sample = new FrameSample(Key, new RgbImage(new byte[8 * 8 * 3], 8, 8));
        var result = await analyzer.AnalyzeAsync(sample, CancellationToken.None);
        store.Set(result);

        var stored = store.Get(Key, "ocr");
        Assert.NotNull(stored);
        Assert.Equal("hello", stored!.Primitives[0].Text);
    }

    private static AnalysisResult Result(string analyzer, int primitives = 1, DateTimeOffset? at = null) => new(
        analyzer,
        Key,
        at ?? DateTimeOffset.UtcNow,
        5,
        640,
        480,
        Enumerable.Range(0, primitives)
            .Select(index => new AnalysisPrimitive(AnalysisKind.Box, $"obj{index}", 0.5, index, index, index, 10, 10))
            .ToArray());

    private sealed class FakeAnalyzer(string id, bool enabled) : IStreamAnalyzer
    {
        public string Id => id;

        public string DisplayName => id;

        public bool Enabled => enabled;

        public Task<AnalysisResult> AnalyzeAsync(FrameSample frame, CancellationToken cancellationToken) =>
            Task.FromResult(new AnalysisResult(
                id,
                frame.Key,
                DateTimeOffset.UtcNow,
                1,
                frame.Image.Width,
                frame.Image.Height,
                [new AnalysisPrimitive(AnalysisKind.Text, "text", 0.9, null, 1, 1, Text: "hello")]));
    }
}
