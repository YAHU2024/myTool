using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class MangaSceneRoutingServiceTests
{
    [Fact]
    public async Task Disabled_DoesNotWriteTemporaryImage()
    {
        var service = new MangaSceneRoutingService(new MangaWorkerClient("missing", "missing"));
        var image = new OcrImage(2, 2, 8, new byte[16]);
        var result = await service.ProbeAsync(image, "disabled-test", "en", false, TimeSpan.FromSeconds(1));
        Assert.Equal(ScreenshotSceneRoute.StandardOcr, result.Route);
        Assert.Null(result.TemporaryImagePath);
    }

    [Fact]
    public async Task OversizeImage_FallsBackBeforeStartingWorker()
    {
        var service = new MangaSceneRoutingService(new MangaWorkerClient("missing", "missing"));
        var image = new OcrImage(10, 10, 40, new byte[400]);
        var result = await service.ProbeAsync(image, "oversize-test", "en", true, TimeSpan.FromSeconds(1), maxPixels: 10);
        Assert.Equal(ScreenshotSceneRoute.FallbackStandardOcr, result.Route);
        Assert.Null(result.TemporaryImagePath);
    }
}
