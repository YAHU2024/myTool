namespace QuickTranslate.Models;

/// <summary>截图译文覆盖层中的一个已安全映射文本单元。</summary>
public sealed record ScreenshotOverlayItem(
    OcrBounds Bounds,
    string Translation,
    IReadOnlyList<OcrPoint>? Polygon = null,
    string UnitId = "",
    double? Confidence = null);

public enum ScreenshotOverlayLayoutStatus
{
    Placed,
    Degraded,
    Skipped
}

/// <summary>一个译文单元经过物理像素布局后的结果。</summary>
public sealed record ScreenshotOverlayLayout(
    string UnitId,
    OcrBounds SourceBounds,
    OcrBounds LayoutBounds,
    string Translation,
    double FontSize,
    int LineCount,
    double MeasuredTextWidth,
    double MeasuredTextHeight,
    ScreenshotOverlayLayoutStatus Status,
    string? DegradationReason = null,
    IReadOnlyList<OcrPoint>? Polygon = null)
{
    public bool IsTextFullyContained =>
        Status != ScreenshotOverlayLayoutStatus.Skipped &&
        MeasuredTextWidth <= LayoutBounds.Width + 0.01 &&
        MeasuredTextHeight <= LayoutBounds.Height + 0.01;
}

/// <summary>覆盖层布局的物理像素策略。字号和内边距均以物理像素表达。</summary>
public sealed record ScreenshotOverlayLayoutOptions(
    double MinFontSize = 10,
    double MaxFontSize = 28,
    double PreferredFontSizeRatio = 0.62,
    double FontSizeStep = 0.5,
    double HorizontalPadding = 8,
    double VerticalPadding = 6,
    double LineHeightRatio = 1.35,
    int MinimumBoxWidth = 24,
    int MinimumBoxHeight = 20,
    int CollisionGap = 2)
{
    public void Validate()
    {
        if (!double.IsFinite(MinFontSize) || !double.IsFinite(MaxFontSize) ||
            !double.IsFinite(PreferredFontSizeRatio) || !double.IsFinite(FontSizeStep) ||
            !double.IsFinite(HorizontalPadding) || !double.IsFinite(VerticalPadding) ||
            !double.IsFinite(LineHeightRatio) ||
            MinFontSize <= 0 || MaxFontSize < MinFontSize ||
            PreferredFontSizeRatio <= 0 || FontSizeStep <= 0 ||
            HorizontalPadding < 0 || VerticalPadding < 0 ||
            LineHeightRatio < 1 || MinimumBoxWidth <= 0 ||
            MinimumBoxHeight <= 0 || CollisionGap < 0)
        {
            throw new ArgumentException("覆盖层布局策略无效。", nameof(MinFontSize));
        }
    }
}

public sealed record ScreenshotOverlayLayoutResult(
    IReadOnlyList<ScreenshotOverlayLayout> Items)
{
    public int PlacedCount => Items.Count(static item => item.Status == ScreenshotOverlayLayoutStatus.Placed);

    public int DegradedCount => Items.Count(static item => item.Status == ScreenshotOverlayLayoutStatus.Degraded);

    public int SkippedCount => Items.Count(static item => item.Status == ScreenshotOverlayLayoutStatus.Skipped);
}
