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
        if (block.OrientationDegrees is { } orientation &&
            (double.IsNaN(orientation) || double.IsInfinity(orientation) || orientation is < -360 or > 360))
        {
            throw new ArgumentException("OCR 块方向必须为空或位于 -360 到 360 度之间。", nameof(block));
        }
        if (block.Polygon is { } polygon)
            ValidatePolygon(polygon, pixelWidth, pixelHeight, block);
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

    private static void ValidatePolygon(
        IReadOnlyList<OcrPoint> polygon,
        int pixelWidth,
        int pixelHeight,
        OcrTextBlock block)
    {
        if (polygon.Count < 4)
            throw new ArgumentException("OCR 块多边形至少需要四个点。", nameof(block));

        var twiceArea = 0d;
        for (var index = 0; index < polygon.Count; index++)
        {
            var current = polygon[index];
            var next = polygon[(index + 1) % polygon.Count];
            if (!current.IsFinite || current.X < 0 || current.Y < 0 ||
                current.X > pixelWidth || current.Y > pixelHeight)
            {
                throw new ArgumentException("OCR 块多边形点必须为有限值并落在图像范围内。", nameof(block));
            }

            twiceArea += current.X * next.Y - next.X * current.Y;
        }

        if (Math.Abs(twiceArea) < 0.5)
            throw new ArgumentException("OCR 块多边形面积必须大于 0。", nameof(block));
    }
}
