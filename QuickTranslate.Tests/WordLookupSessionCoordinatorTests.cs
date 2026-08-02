using QuickTranslate.Core;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class WordLookupSessionCoordinatorTests
{
    [Fact]
    public void Begin_CancelsPreviousAndRejectsItsLateCompletion()
    {
        using var coordinator = new WordLookupSessionCoordinator();
        var first = coordinator.Begin("first");
        var second = coordinator.Begin("second");

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(coordinator.TryComplete(first, Result("first")));
        Assert.True(coordinator.TryComplete(second, Result("second")));
        Assert.Equal("second", coordinator.Current.Result?.Headword);
        Assert.Equal(WordLookupSessionStatus.Completed, coordinator.Current.Status);
    }

    [Fact]
    public void FailureNotFoundAndCancel_AreDistinctStates()
    {
        using var coordinator = new WordLookupSessionCoordinator();
        var notFound = coordinator.Begin("unknown");
        Assert.True(coordinator.TryNotFound(notFound));
        Assert.Equal(WordLookupSessionStatus.NotFound, coordinator.Current.Status);

        var failed = coordinator.Begin("failed");
        Assert.True(coordinator.TryFail(failed, "查询失败"));
        Assert.Equal(WordLookupSessionStatus.Failed, coordinator.Current.Status);

        coordinator.Begin("cancelled");
        coordinator.CancelCurrent();
        Assert.Equal(WordLookupSessionStatus.Cancelled, coordinator.Current.Status);
    }

    private static WordLookupResult Result(string headword) => new(
        headword,
        Array.Empty<WordPronunciation>(),
        [new WordSense("", "definition", "")],
        Array.Empty<WordExample>(),
        Array.Empty<string>(),
        new WordLookupSource("fake", "fake", WordLookupSourceKind.Dictionary));
}
