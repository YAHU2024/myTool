namespace QuickTranslate.Models;

/// <summary>截图译文覆盖层中的一个已安全映射文本单元。</summary>
public sealed record ScreenshotOverlayItem(
    OcrBounds Bounds,
    string Translation,
    IReadOnlyList<OcrPoint>? Polygon = null);
