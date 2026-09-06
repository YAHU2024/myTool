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

    [Fact]
    public void CreateFromDetectedLanguage_MapsJapanese()
    {
        var request = MangaWorkerRequestFactory.CreateFromDetectedLanguage("r2", ".\\image.png", ScreenshotSourceLanguage.Japanese, false);
        Assert.Equal("ja", request.SourceLanguage);
    }

    [Fact]
    public void WorkerResponse_CreatesTranslationUnits()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{\"type\":\"completed\",\"blocks\":[{\"block_id\":\"b1\",\"bounds\":[1,2,11,22],\"source_text\":\"hello\"}]}" );
        var response = new MangaWorkerResponse("r1", "completed", null, doc.RootElement.Clone());
        var units = MangaTranslationOrchestrator.CreateUnits(response);
        Assert.Single(units);
        Assert.Equal("b1", units[0].UnitId);
        Assert.Equal(10, units[0].Bounds.Width);
    }
}
