using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ScreenshotTranslationSessionStateTests
{
    [Fact]
    public void CancelledSessionDoesNotBlockNextSession()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.False(ScreenshotTranslationSessionState.BlocksNewSession(source));
    }

    [Fact]
    public void StaleSessionCannotReleaseNewSession()
    {
        using var stale = new CancellationTokenSource();
        using var current = new CancellationTokenSource();
        CancellationTokenSource? active = current;

        var released = ScreenshotTranslationSessionState.TryRelease(ref active, stale);

        Assert.False(released);
        Assert.Same(current, active);
    }

    [Fact]
    public void CurrentSessionCanReleaseEntryGate()
    {
        using var source = new CancellationTokenSource();
        CancellationTokenSource? active = source;

        var released = ScreenshotTranslationSessionState.TryRelease(ref active, source);

        Assert.True(released);
        Assert.Null(active);
    }

    [Fact]
    public void RestoreUiIsAllowedAfterCurrentSessionRelease()
    {
        Assert.True(ScreenshotTranslationSessionState.ShouldRestoreUi(
            isCurrentSession: true,
            releaseClaimed: false,
            hasSelectionWindow: false,
            hasProgressWindow: false,
            hasOverlayWindow: false));
    }

    [Fact]
    public void RestoreUiIsAllowedAfterCancellationRelease()
    {
        Assert.True(ScreenshotTranslationSessionState.ShouldRestoreUi(
            isCurrentSession: false,
            releaseClaimed: true,
            hasSelectionWindow: false,
            hasProgressWindow: false,
            hasOverlayWindow: false));
    }

    [Fact]
    public void RestoreUiIsBlockedWhenAnotherScreenshotSessionExists()
    {
        Assert.False(ScreenshotTranslationSessionState.ShouldRestoreUi(
            isCurrentSession: false,
            releaseClaimed: true,
            hasSelectionWindow: true,
            hasProgressWindow: false,
            hasOverlayWindow: false));
        Assert.False(ScreenshotTranslationSessionState.ShouldRestoreUi(
            isCurrentSession: false,
            releaseClaimed: true,
            hasSelectionWindow: false,
            hasProgressWindow: true,
            hasOverlayWindow: false));
        Assert.False(ScreenshotTranslationSessionState.ShouldRestoreUi(
            isCurrentSession: false,
            releaseClaimed: true,
            hasSelectionWindow: false,
            hasProgressWindow: false,
            hasOverlayWindow: true));
    }
}
