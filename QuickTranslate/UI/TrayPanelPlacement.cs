using QuickTranslate.Core;

namespace QuickTranslate.UI;

public readonly record struct PhysicalSize(int Width, int Height);

public readonly record struct PhysicalRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

public static class TrayPanelPlacement
{
    public static PhysicalRect Calculate(
        PhysicalRect workArea,
        PhysicalPoint anchor,
        PhysicalSize panelSize,
        double dpiX,
        double dpiY,
        double marginDip = 12)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea));
        if (panelSize.Width <= 0 || panelSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(panelSize));
        if (dpiX <= 0 || dpiY <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiX));

        var marginX = (int)Math.Round(marginDip * dpiX / 96d);
        var marginY = (int)Math.Round(marginDip * dpiY / 96d);
        var desiredLeft = anchor.X - panelSize.Width / 2;
        var rightAligned = workArea.Right - marginX - panelSize.Width;
        var left = anchor.X >= workArea.Left + workArea.Width / 2
            ? rightAligned
            : desiredLeft;
        var top = workArea.Bottom - marginY - panelSize.Height;

        var maxLeft = Math.Max(workArea.Left, workArea.Right - panelSize.Width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - panelSize.Height);
        left = Math.Clamp(left, workArea.Left, maxLeft);
        top = Math.Clamp(top, workArea.Top, maxTop);
        return new PhysicalRect(left, top, panelSize.Width, panelSize.Height);
    }
}
