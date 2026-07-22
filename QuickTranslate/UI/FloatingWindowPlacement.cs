using System.Windows;

namespace QuickTranslate.UI;

internal static class FloatingWindowPlacement
{
    public static bool ShouldPlaceAbove(
        Rect exclusionBounds,
        Rect workArea,
        double requiredHeight,
        double gap)
    {
        var spaceBelow = Math.Max(0, workArea.Bottom - exclusionBounds.Bottom - gap);
        var spaceAbove = Math.Max(0, exclusionBounds.Top - gap - workArea.Top);

        // Preserve the established UX: prefer below whenever the current window
        // fits there. Only move above when below is genuinely too small.
        if (spaceBelow >= requiredHeight)
            return false;
        if (spaceAbove >= requiredHeight)
            return true;
        return spaceBelow < spaceAbove;
    }

    public static Rect Calculate(
        Point anchor,
        Rect exclusionBounds,
        Size windowSize,
        Rect workArea,
        bool placeAbove,
        double gap)
    {
        var left = anchor.X - windowSize.Width / 2;
        var top = placeAbove
            ? exclusionBounds.Top - gap - windowSize.Height
            : exclusionBounds.Bottom + gap;

        if (left + windowSize.Width > workArea.Right)
            left = workArea.Right - windowSize.Width;
        if (left < workArea.Left)
            left = workArea.Left;
        if (top + windowSize.Height > workArea.Bottom)
            top = workArea.Bottom - windowSize.Height;
        if (top < workArea.Top)
            top = workArea.Top;

        return new Rect(left, top, windowSize.Width, windowSize.Height);
    }
}
