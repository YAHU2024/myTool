using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class MangaWorkerRequestFactoryTests
{
    [Fact]
    public void Create_AddsInpaintStageAndNormalizesPaths()
    {
        var request = MangaWorkerRequestFactory.Create("r1", ".\\image.png", "ja", true, ".\\out");
        Assert.Equal("quicktranslate.manga-worker.v1", request.Schema);
        Assert.Equal(new[] { "detect", "ocr", "inpaint" }, request.Stages);
        Assert.True(Path.IsPathFullyQualified(request.ImagePath));
        Assert.True(Path.IsPathFullyQualified(request.OutputDirectory!));
    }
}
