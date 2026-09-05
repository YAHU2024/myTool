using QuickTranslate.Models;

namespace QuickTranslate.Core;

/// <summary>为复杂场景 OCR 块生成确定性的横排/竖排阅读顺序。</summary>
public static class OcrReadingOrderSorter
{
    public static IReadOnlyList<OcrTextBlock> Sort(IReadOnlyList<OcrTextBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var ordered = blocks
            .Select((block, index) => (block, index))
            .OrderBy(item => IsVertical(item.block) ? 1 : 0)
            .ThenBy(item => IsVertical(item.block) ? item.block.Bounds.X : item.block.Bounds.Y)
            .ThenBy(item => IsVertical(item.block) ? item.block.Bounds.Y : item.block.Bounds.X)
            .ThenBy(item => item.index)
            .Select((item, index) => item.block with { ReadingOrder = index + 1 })
            .ToArray();
        return ordered;
    }

    private static bool IsVertical(OcrTextBlock block)
    {
        var angle = block.OrientationDegrees ?? 0;
        var normalized = Math.Abs(angle % 180);
        if (normalized > 90) normalized = 180 - normalized;
        if (normalized >= 60) return true;
        return block.Bounds.Height > block.Bounds.Width * 1.35;
    }
}
