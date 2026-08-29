namespace QuickTranslate.Core;

/// <summary>
/// 屏幕截图使用的物理像素矩形。坐标相对于虚拟桌面左上角，可包含负值。
/// </summary>
public readonly record struct ScreenshotRegion(int Left, int Top, int Width, int Height)
{
    public int Right => checked(Left + Width);

    public int Bottom => checked(Top + Height);

    public bool IsValid => Width > 0 && Height > 0;

    public bool Contains(PhysicalPoint point) =>
        IsValid &&
        (long)point.X >= Left && (long)point.X < (long)Left + Width &&
        (long)point.Y >= Top && (long)point.Y < (long)Top + Height;

    public bool Contains(ScreenshotRegion region) =>
        IsValid && region.IsValid &&
        (long)region.Left >= Left &&
        (long)region.Top >= Top &&
        (long)region.Left + region.Width <= (long)Left + Width &&
        (long)region.Top + region.Height <= (long)Top + Height;
}

public enum ScreenshotSelectionRejection
{
    None,
    InvalidMonitor,
    StartOutsideMonitor,
    EndOutsideMonitor,
    TooSmall,
    ExceedsResourceLimit,
    CoordinateOverflow
}

public readonly record struct ScreenshotSelectionDecision(
    bool IsAccepted,
    ScreenshotRegion Region,
    ScreenshotSelectionRejection Rejection)
{
    public string Message => Rejection switch
    {
        ScreenshotSelectionRejection.StartOutsideMonitor or
        ScreenshotSelectionRejection.EndOutsideMonitor => "截图区域必须完全位于同一显示器内。",
        ScreenshotSelectionRejection.TooSmall => "截图区域过小，请至少框选 24 个物理像素。",
        ScreenshotSelectionRejection.ExceedsResourceLimit => "截图区域超过当前处理上限，请缩小后重试。",
        ScreenshotSelectionRejection.InvalidMonitor => "无法确定当前显示器，请重试。",
        ScreenshotSelectionRejection.CoordinateOverflow => "截图坐标超出系统支持范围，请重试。",
        _ => string.Empty
    };

    public static ScreenshotSelectionDecision Accepted(ScreenshotRegion region) =>
        new(true, region, ScreenshotSelectionRejection.None);

    public static ScreenshotSelectionDecision Rejected(ScreenshotSelectionRejection rejection) =>
        new(false, default, rejection);
}

public static class ScreenshotSelectionModel
{
    public const int MinimumDimension = 24;

    public static ScreenshotSelectionDecision Evaluate(
        PhysicalPoint start,
        PhysicalPoint end,
        ScreenshotRegion monitor,
        int minimumDimension = MinimumDimension,
        Models.OcrResourceLimits? resourceLimits = null)
    {
        if (!monitor.IsValid)
            return ScreenshotSelectionDecision.Rejected(ScreenshotSelectionRejection.InvalidMonitor);
        if (minimumDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumDimension));
        if (!monitor.Contains(start))
            return ScreenshotSelectionDecision.Rejected(ScreenshotSelectionRejection.StartOutsideMonitor);
        if (!monitor.Contains(end))
            return ScreenshotSelectionDecision.Rejected(ScreenshotSelectionRejection.EndOutsideMonitor);

        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var right = Math.Max(start.X, end.X);
        var bottom = Math.Max(start.Y, end.Y);
        var width = (long)right - left;
        var height = (long)bottom - top;
        if (width <= 0 || height <= 0 || width < minimumDimension || height < minimumDimension)
            return ScreenshotSelectionDecision.Rejected(ScreenshotSelectionRejection.TooSmall);
        if (width > int.MaxValue || height > int.MaxValue)
            return ScreenshotSelectionDecision.Rejected(ScreenshotSelectionRejection.CoordinateOverflow);

        var region = new ScreenshotRegion(left, top, (int)width, (int)height);
        if (!monitor.Contains(region))
            return ScreenshotSelectionDecision.Rejected(ScreenshotSelectionRejection.CoordinateOverflow);

        if (resourceLimits is not null &&
            ((long)region.Width * region.Height > resourceLimits.MaxPixelCount ||
             region.Width > resourceLimits.MaxImageDimension ||
             region.Height > resourceLimits.MaxImageDimension ||
             (long)region.Width * 4 * region.Height > resourceLimits.MaxPayloadBytes))
        {
            return ScreenshotSelectionDecision.Rejected(ScreenshotSelectionRejection.ExceedsResourceLimit);
        }

        return ScreenshotSelectionDecision.Accepted(region);
    }
}
