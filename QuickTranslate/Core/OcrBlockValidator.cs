using QuickTranslate.Models;

namespace QuickTranslate.Core;

/// <summary>校验 OCR 块的文本、几何范围和一次结果内的唯一性。</summary>
public static class OcrBlockValidator
{
    public static void Validate(OcrTextBlock block, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (pixelWidth <= 0 || pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "图像尺寸必须大于 0。");
        if (string.IsNullOrWhiteSpace(block.BlockId))
            throw new ArgumentException("OCR 块 ID 不能为空。", nameof(block));
        if (string.IsNullOrWhiteSpace(block.Text))
            throw new ArgumentException("OCR 块文本不能为空。", nameof(block));
        if (!block.Bounds.IsWithin(pixelWidth, pixelHeight))
            throw new ArgumentException("OCR 块矩形必须为正数并落在图像范围内。", nameof(block));
        if (block.Confidence is { } confidence &&
            (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence is < 0 or > 1))
        {
            throw new ArgumentException("OCR 块置信度必须为空或位于 0 到 1 之间。", nameof(block));
        }
    }

    public static void Validate(OcrTextBlock block, OcrImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        image.Validate();
        Validate(block, image.PixelWidth, image.PixelHeight);
    }

    public static void ValidateAll(IReadOnlyList<OcrTextBlock> blocks, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            Validate(block, pixelWidth, pixelHeight);
            if (!ids.Add(block.BlockId))
                throw new ArgumentException($"OCR 块 ID 重复：{block.BlockId}。", nameof(blocks));
        }
    }

    public static void ValidateAll(IReadOnlyList<OcrTextBlock> blocks, OcrImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        image.Validate();
        ValidateAll(blocks, image.PixelWidth, image.PixelHeight);
    }
}
