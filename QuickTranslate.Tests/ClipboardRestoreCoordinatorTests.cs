using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ClipboardRestoreCoordinatorTests
{
    [Theory]
    [InlineData(null, 42u, true)]
    [InlineData("", 42u, true)]
    [InlineData("original", 0u, true)]
    [InlineData("original", 42u, false)]
    public void ShouldQueue_RejectsIncompleteRestoreContext(
        string? originalText,
        uint copiedSequence,
        bool restoreRequested)
    {
        Assert.False(ClipboardRestoreCoordinator.ShouldQueue(
            originalText,
            copiedSequence,
            restoreRequested));
    }

    [Fact]
    public void ShouldQueue_AcceptsTextWithValidSequence()
    {
        Assert.True(ClipboardRestoreCoordinator.ShouldQueue("original", 42u, true));
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 250)]
    [InlineData(3, 500)]
    [InlineData(4, 0)]
    public void RetryDelay_UsesBoundedBackoff(int attempt, int expectedMilliseconds)
    {
        Assert.Equal(
            expectedMilliseconds,
            ClipboardRestoreCoordinator.GetRetryDelayMilliseconds(attempt));
    }
}
