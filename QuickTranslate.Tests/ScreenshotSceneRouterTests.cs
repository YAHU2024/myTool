using QuickTranslate.Services;
using QuickTranslate.Models;

namespace QuickTranslate.Tests;

public sealed class ScreenshotSceneRouterTests
{
    [Fact]
    public void FromOcrResult_ComputesBubbleEvidence()
    {
        var result = new OcrResult(new[]
        {
            new OcrTextBlock("b1", "a", new(0, 0, 10, 10), RegionType: OcrRegionType.Bubble),
            new OcrTextBlock("b2", "b", new(0, 0, 10, 10), RegionType: OcrRegionType.Bubble),
            new OcrTextBlock("b3", "c", new(0, 0, 10, 10))
        }, "en", false, 0, TimeSpan.Zero);
        var evidence = ScreenshotSceneRouter.FromOcrResult(result, true);
        Assert.Equal(3, evidence.BlockCount);
        Assert.Equal(2, evidence.BubbleBlockCount);
        Assert.True(evidence.MangaScore >= .70);
    }
    [Fact]
    public void Disabled_UsesStandardOcr() =>
        Assert.Equal(ScreenshotSceneRoute.StandardOcr,
            ScreenshotSceneRouter.Decide(false, new(5, 5, 1, true)));

    [Fact]
    public void StrongBubbleEvidence_UsesMangaWorker() =>
        Assert.Equal(ScreenshotSceneRoute.MangaWorker,
            ScreenshotSceneRouter.Decide(true, new(5, 4, .8, true)));

    [Fact]
    public void MissingWorker_FallsBack() =>
        Assert.Equal(ScreenshotSceneRoute.FallbackStandardOcr,
            ScreenshotSceneRouter.Decide(true, new(5, 4, .8, false)));

    [Fact]
    public void WeakEvidence_UsesStandardOcr() =>
        Assert.Equal(ScreenshotSceneRoute.StandardOcr,
            ScreenshotSceneRouter.Decide(true, new(5, 1, .8, true)));
}
