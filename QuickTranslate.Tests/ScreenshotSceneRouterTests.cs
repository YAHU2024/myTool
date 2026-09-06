using QuickTranslate.Services;

namespace QuickTranslate.Tests;

public sealed class ScreenshotSceneRouterTests
{
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
