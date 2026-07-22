using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public class TranslationCacheServiceTests
{
    [Fact]
    public void CacheKey_ChangesWhenPromptModelOrModeChanges()
    {
        var cache = new TranslationCacheService();
        var translation = CreateRequest("model-a", "Translate to English.");
        var changedPrompt = translation with { SystemPrompt = "Explain in English." };
        var changedModel = translation with { ModelName = "model-b" };
        var analysis = translation with { Kind = TranslationRequestKind.Analysis, ContentType = ContentType.Analysis };

        cache.Set(translation, "result");

        Assert.True(cache.TryGet(translation, out var result));
        Assert.Equal("result", result);
        Assert.False(cache.TryGet(changedPrompt, out _));
        Assert.False(cache.TryGet(changedModel, out _));
        Assert.False(cache.TryGet(analysis, out _));
    }

    [Fact]
    public void CacheKey_DoesNotContainApiKey()
    {
        var cache = new TranslationCacheService();
        var first = CreateRequest("model-a", "Translate to English.") with { ApiKey = "key-a" };
        var second = first with { ApiKey = "key-b" };

        cache.Set(first, "result");

        Assert.True(cache.TryGet(second, out var result));
        Assert.Equal("result", result);
    }

    [Fact]
    public void Cache_ExpiresEntries()
    {
        var clock = new ManualTimeProvider();
        var cache = new TranslationCacheService(
            capacity: 10,
            ttl: TimeSpan.FromMinutes(30),
            timeProvider: clock);
        var request = CreateRequest("model-a", "Translate to English.");

        cache.Set(request, "result");
        clock.Advance(TimeSpan.FromMinutes(30));

        Assert.False(cache.TryGet(request, out _));
    }

    [Fact]
    public void Cache_EvictsLeastRecentlyUsedEntryWhenFull()
    {
        var cache = new TranslationCacheService(capacity: 2);
        var first = CreateRequest("model-a", "first");
        var second = CreateRequest("model-a", "second");
        var third = CreateRequest("model-a", "third");

        cache.Set(first, "1");
        cache.Set(second, "2");
        Assert.True(cache.TryGet(first, out _));
        cache.Set(third, "3");

        Assert.True(cache.TryGet(first, out _));
        Assert.False(cache.TryGet(second, out _));
        Assert.True(cache.TryGet(third, out _));
    }

    [Fact]
    public void Cache_IgnoresEmptyResults()
    {
        var cache = new TranslationCacheService();
        var request = CreateRequest("model-a", "prompt");

        cache.Set(request, " ");

        Assert.False(cache.TryGet(request, out _));
    }

    private static TranslationRequest CreateRequest(string model, string prompt)
    {
        return new TranslationRequest(
            TranslationRequestKind.Translation,
            "hello",
            "简体中文",
            ContentType.Translation,
            "https://example.test/v1",
            "api-key",
            model,
            prompt,
            false);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
