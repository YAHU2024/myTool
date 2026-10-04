namespace QuickTranslate.Core;

/// <summary>
/// Keeps the screenshot translation entry gate independent from a cancelled
/// session that is still finishing its asynchronous cleanup.
/// </summary>
internal static class ScreenshotTranslationSessionState
{
    public static bool BlocksNewSession(CancellationTokenSource? source) =>
        source is { IsCancellationRequested: false };

    public static bool ShouldRestoreUi(
        bool isCurrentSession,
        bool releaseClaimed,
        bool hasSelectionWindow,
        bool hasProgressWindow,
        bool hasOverlayWindow) =>
        (isCurrentSession || releaseClaimed) &&
        !hasSelectionWindow &&
        !hasProgressWindow &&
        !hasOverlayWindow;

    public static bool TryRelease(
        ref CancellationTokenSource? activeSource,
        CancellationTokenSource expectedSource)
    {
        ArgumentNullException.ThrowIfNull(expectedSource);
        if (!ReferenceEquals(activeSource, expectedSource))
            return false;

        activeSource = null;
        return true;
    }
}
