namespace QuickTranslate.Core;

/// <summary>
/// Keeps the floating result view pinned to its end until the user browses away.
/// This class deliberately has no WPF dependency so the input event mapping can
/// stay in the window while the policy remains testable.
/// </summary>
public sealed class AutoScrollController
{
    public const double BottomThresholdDip = 20;

    public bool IsAutoScrollEnabled { get; private set; } = true;

    /// <summary>
    /// Starts a replacement result. New streamed content should initially follow
    /// the end of the document.
    /// </summary>
    public bool BeginRequest()
    {
        IsAutoScrollEnabled = true;
        return true;
    }

    /// <summary>
    /// Handles content or viewport changes. A content append must not disable
    /// follow mode before the view can be scrolled to its new end.
    /// </summary>
    public bool OnContentOrViewportChanged() => IsAutoScrollEnabled;

    /// <summary>
    /// Handles a user-originated scroll position. Reaching the bottom threshold
    /// resumes following; browsing elsewhere pauses it.
    /// </summary>
    public bool OnUserScrollPositionChanged(
        double verticalOffset,
        double viewportHeight,
        double extentHeight)
    {
        IsAutoScrollEnabled = IsNearBottom(verticalOffset, viewportHeight, extentHeight);
        return IsAutoScrollEnabled;
    }

    /// <summary>
    /// Pauses following for user input that intentionally moves toward earlier
    /// content, even before WPF reports the resulting scroll metrics.
    /// </summary>
    public void PauseForUpwardNavigation() => IsAutoScrollEnabled = false;

    /// <summary>
    /// Explicitly resumes following and requests that the caller scroll to end.
    /// </summary>
    public bool Resume()
    {
        IsAutoScrollEnabled = true;
        return true;
    }

    public static bool IsNearBottom(
        double verticalOffset,
        double viewportHeight,
        double extentHeight)
    {
        if (double.IsNaN(verticalOffset) || double.IsNaN(viewportHeight) || double.IsNaN(extentHeight) ||
            double.IsInfinity(verticalOffset) || double.IsInfinity(viewportHeight) || double.IsInfinity(extentHeight))
        {
            return false;
        }

        var remaining = extentHeight - viewportHeight - verticalOffset;
        return remaining <= BottomThresholdDip;
    }
}
