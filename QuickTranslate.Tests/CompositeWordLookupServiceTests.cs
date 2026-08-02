using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class CompositeWordLookupServiceTests
{
    [Fact]
    public async Task Lookup_FallsBackToAi_WhenLocalDictionaryNotFound()
    {
        var local = new StubLookupService(_ => throw new WordLookupNotFoundException());
        var ai = new StubLookupService(_ => Task.FromResult(Result("ai")));
        var service = new CompositeWordLookupService(local, ai);

        var result = await service.LookupAsync(
            new WordLookupRequest("chatgpt", "简体中文"),
            CancellationToken.None);

        Assert.Equal("ai", result.Headword);
        Assert.Equal(1, local.CallCount);
        Assert.Equal(1, ai.CallCount);
    }

    [Fact]
    public async Task Lookup_DoesNotCallFallback_WhenPrimaryReturnsResult()
    {
        var local = new StubLookupService(_ => Task.FromResult(Result("local")));
        var ai = new StubLookupService(_ => Task.FromResult(Result("ai")));
        var service = new CompositeWordLookupService(local, ai);

        var result = await service.LookupAsync(
            new WordLookupRequest("hello", "简体中文"),
            CancellationToken.None);

        Assert.Equal("local", result.Headword);
        Assert.Equal(1, local.CallCount);
        Assert.Equal(0, ai.CallCount);
    }

    [Fact]
    public async Task Lookup_DoesNotFallback_WhenPrimaryCancellationOccurs()
    {
        var local = new StubLookupService(_ => throw new OperationCanceledException());
        var ai = new StubLookupService(_ => Task.FromResult(Result("ai")));
        var service = new CompositeWordLookupService(local, ai);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.LookupAsync(
                new WordLookupRequest("hello", "简体中文"),
                CancellationToken.None));

        Assert.Equal(1, local.CallCount);
        Assert.Equal(0, ai.CallCount);
    }

    private static WordLookupResult Result(string headword) => new(
        headword,
        Array.Empty<WordPronunciation>(),
        new[]
        {
            new WordSense("noun", "释义", "definition")
        },
        Array.Empty<WordExample>(),
        Array.Empty<string>(),
        new WordLookupSource("fake", "fake", WordLookupSourceKind.AiGenerated));

    private sealed class StubLookupService : IWordLookupService
    {
        private readonly Func<WordLookupRequest, Task<WordLookupResult>> _handler;

        public StubLookupService(Func<WordLookupRequest, Task<WordLookupResult>> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }

        public Task<WordLookupResult> LookupAsync(
            WordLookupRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return _handler(request);
        }
    }
}
