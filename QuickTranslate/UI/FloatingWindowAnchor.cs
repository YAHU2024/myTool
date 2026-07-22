using System.Windows;

namespace QuickTranslate.UI;

/// <summary>
/// Physical-pixel placement input for the floating window.
/// </summary>
internal readonly record struct FloatingWindowAnchor(
    Point PreferredPoint,
    Rect ExclusionBounds)
{
    public Rect GetEffectiveExclusionBounds(Point scale)
    {
        if (!ExclusionBounds.IsEmpty && ExclusionBounds.Width > 0 && ExclusionBounds.Height > 0)
            return ExclusionBounds;

        // UIA can be unavailable. Reserve a small physical area around the
        // cursor/drag endpoint so the fallback popup does not sit on the pointer.
        var halfWidth = 12 * scale.X;
        var halfHeight = 12 * scale.Y;
        return new Rect(
            PreferredPoint.X - halfWidth,
            PreferredPoint.Y - halfHeight,
            halfWidth * 2,
            halfHeight * 2);
    }
}
