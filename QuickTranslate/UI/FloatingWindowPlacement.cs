using System.Windows;

namespace QuickTranslate.UI;

internal static class FloatingWindowPlacement
{
    public static bool ShouldPlaceAbove(Point anchor, Rect workArea, double gap)
    {
        var spaceBelow = Math.Max(0, workArea.Bottom - (anchor.Y + gap));
        var spaceAbove = Math.Max(0, anchor.Y - gap - workArea.Top);
        return spaceBelow < spaceAbove;
    }

    public static Rect Calculate(
        Point anchor,
        Size windowSize,
        Rect workArea,
        bool placeAbove,
        double gap)
    {
        var left = anchor.X - windowSize.Width / 2;
        var top = placeAbove
            ? anchor.Y - gap - windowSize.Height
            : anchor.Y + gap;

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
