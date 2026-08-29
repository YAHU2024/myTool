using QuickTranslate.Models;

namespace QuickTranslate.Core;

public sealed record OcrParagraph(
    string ParagraphId,
    string SourceText,
    IReadOnlyList<OcrTextBlock> Lines,
    OcrBounds Bounds);

/// <summary>
/// 将 OCR 块按确定性的阅读顺序聚合为行和段落。
/// </summary>
public static class OcrBlockAggregator
{
    private const double MinimumVerticalOverlap = 0.35;
    private const double MaximumParagraphGapRatio = 0.75;
    private const int MinimumParagraphGapPixels = 4;

    public static IReadOnlyList<OcrParagraph> Aggregate(
        IReadOnlyList<OcrTextBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count == 0)
            return Array.Empty<OcrParagraph>();

        var maxRight = blocks.Max(static block => (long)block.Bounds.X + block.Bounds.Width);
        var maxBottom = blocks.Max(static block => (long)block.Bounds.Y + block.Bounds.Height);
        if (maxRight > int.MaxValue || maxBottom > int.MaxValue)
            throw new ArgumentException("OCR 块坐标超出支持范围。", nameof(blocks));
        OcrBlockValidator.ValidateAll(blocks, (int)maxRight, (int)maxBottom);

        var lines = BuildLines(blocks);
        var paragraphs = BuildParagraphs(lines);
        return paragraphs
            .Select((paragraph, index) => new OcrParagraph(
                $"p{index + 1:0000}",
                OcrTextNormalizer.Join(paragraph.Select(static line => line.Text)),
                paragraph.SelectMany(static line => line.Blocks).ToArray(),
                paragraph.AggregateBounds()))
            .ToArray();
    }

    private static List<AggregatedLine> BuildLines(IReadOnlyList<OcrTextBlock> blocks)
    {
        var lines = new List<AggregatedLine>();
        foreach (var block in blocks
                     .OrderBy(static block => block.Bounds.Y)
                     .ThenBy(static block => block.Bounds.X)
                     .ThenBy(static block => block.BlockId, StringComparer.Ordinal))
        {
            var matchingIndex = -1;
            var bestOverlap = 0d;
            for (var index = 0; index < lines.Count; index++)
            {
                var overlap = VerticalOverlapRatio(block.Bounds, lines[index].Bounds);
                if (overlap >= MinimumVerticalOverlap && overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    matchingIndex = index;
                }
            }

            if (matchingIndex < 0)
            {
                lines.Add(AggregatedLine.Create(block));
                continue;
            }

            lines[matchingIndex].Add(block);
        }

        foreach (var line in lines)
            line.Sort();

        return lines
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ThenBy(static line => line.Blocks[0].BlockId, StringComparer.Ordinal)
            .ToList();
    }

    private static List<List<AggregatedLine>> BuildParagraphs(IReadOnlyList<AggregatedLine> lines)
    {
        var paragraphs = new List<List<AggregatedLine>>();
        foreach (var line in lines)
        {
            if (paragraphs.Count == 0)
            {
                paragraphs.Add(new List<AggregatedLine> { line });
                continue;
            }

            var current = paragraphs[^1];
            var previous = current[^1];
            var gap = line.Bounds.Y - previous.Bounds.Bottom;
            var threshold = Math.Max(
                MinimumParagraphGapPixels,
                (int)Math.Round(Math.Max(line.Bounds.Height, previous.Bounds.Height) * MaximumParagraphGapRatio));
            if (gap <= threshold)
                current.Add(line);
            else
                paragraphs.Add(new List<AggregatedLine> { line });
        }

        return paragraphs;
    }

    private static double VerticalOverlapRatio(OcrBounds first, OcrBounds second)
    {
        var overlap = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Y, second.Y));
        if (overlap == 0)
            return 0;
        return (double)overlap / Math.Min(first.Height, second.Height);
    }

    private sealed class AggregatedLine
    {
        private AggregatedLine(OcrTextBlock block)
        {
            Blocks = new List<OcrTextBlock> { block };
            Bounds = block.Bounds;
        }

        public List<OcrTextBlock> Blocks { get; }

        public OcrBounds Bounds { get; private set; }

        public string Text => OcrTextNormalizer.Join(Blocks.Select(static block => block.Text), " ");

        public static AggregatedLine Create(OcrTextBlock block) => new(block);

        public void Add(OcrTextBlock block)
        {
            Blocks.Add(block);
            Bounds = OcrBounds.Union(Bounds, block.Bounds);
        }

        public void Sort()
        {
            Blocks.Sort(static (first, second) =>
            {
                var x = first.Bounds.X.CompareTo(second.Bounds.X);
                return x != 0 ? x : string.Compare(first.BlockId, second.BlockId, StringComparison.Ordinal);
            });
        }
    }

    private static OcrBounds AggregateBounds(this IEnumerable<AggregatedLine> lines)
    {
        var bounds = default(OcrBounds);
        foreach (var line in lines)
            bounds = OcrBounds.Union(bounds, line.Bounds);
        return bounds;
    }
}
