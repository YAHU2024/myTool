using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class MangaTranslationOrchestratorTests
{
    [Fact]
    public async Task TranslateAsync_MapsProviderResultsByBlockId()
    {
        var provider = new FakeProvider();
        var orchestrator = new MangaTranslationOrchestrator(provider);
        var blocks = new[]
        {
            new MangaTranslationBlock("b1", new(0, 0, 20, 10), "hello", null, "en", "ppocr", null),
            new MangaTranslationBlock("b2", new(0, 20, 20, 10), "world", null, "en", "ppocr", null)
        };
        var result = await orchestrator.TranslateAsync(blocks, "zh-CN");
        Assert.Equal(new[] { "译:b1", "译:b2" }, result.Select(x => x.Translation));
        Assert.Equal("zh-CN", provider.TargetLanguage);
    }

    private sealed class FakeProvider : IScreenshotBatchTranslationService
    {
        public string? TargetLanguage { get; private set; }
        public Task<IReadOnlyList<TranslatedTextUnit>> TranslateScreenshotBatchAsync(IReadOnlyList<ScreenshotTranslationUnit> units, string targetLanguage, CancellationToken cancellationToken = default)
        {
            TargetLanguage = targetLanguage;
            return Task.FromResult<IReadOnlyList<TranslatedTextUnit>>(units.Select(x => new TranslatedTextUnit(x.UnitId, "译:" + x.UnitId)).ToArray());
        }
    }
}
