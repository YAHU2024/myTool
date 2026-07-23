using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public class TranslationCacheRefreshTests
{
    [Fact]
    public void BypassCache_DoesNotRestoreExistingResultOrCountAsMiss()
    {
        var cache = new TranslationCacheService();
        var request = CreateRequest();
        cache.Set(request, "cached result");

        var found = cache.TryGet(request, TranslationCacheReadMode.BypassCache, out var result);

        Assert.False(found);
        Assert.Equal(string.Empty, result);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(0, cache.Misses);
    }

    [Fact]
    public void BypassCache_AllowsFreshResultToReplaceCachedResult()
    {
        var cache = new TranslationCacheService();
        var request = CreateRequest();
        cache.Set(request, "stale result");

        Assert.False(cache.TryGet(request, TranslationCacheReadMode.BypassCache, out _));
        cache.Set(request, "fresh result");

        Assert.True(cache.TryGet(request, out var result));
        Assert.Equal("fresh result", result);
    }

    private static TranslationRequest CreateRequest()
    {
        return new TranslationRequest(
            TranslationRequestKind.Translation,
            "hello",
            "Chinese",
            ContentType.Translation,
            "https://example.test/v1",
            "api-key",
            "model-a",
            "Translate to Chinese.",
            false);
    }
}
