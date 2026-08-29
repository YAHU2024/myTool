using QuickTranslate.Core;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class OcrBlockAggregatorTests
{
    [Fact]
    public void Aggregate_SortsSingleLineBlocksAndJoinsChineseWithoutSpaces()
    {
        var blocks = new[]
        {
            Block("b0002", "地", 30, 10),
            Block("b0001", "本", 10, 10),
            Block("b0003", "翻", 50, 10),
            Block("b0004", "译", 70, 10)
        };

        var paragraph = Assert.Single(OcrBlockAggregator.Aggregate(blocks));

        Assert.Equal("本地翻译", paragraph.SourceText);
        Assert.Equal(new[] { "b0001", "b0002", "b0003", "b0004" }, paragraph.Lines.Select(block => block.BlockId));
    }

    [Fact]
    public void Aggregate_PreservesLatinWordSeparatorAndGroupsNearbyLines()
    {
        var blocks = new[]
        {
            Block("b0002", "world", 70, 10),
            Block("b0001", "Hello", 10, 10),
            Block("b0003", "from OCR", 10, 34)
        };

        var paragraph = Assert.Single(OcrBlockAggregator.Aggregate(blocks));

        Assert.Equal("Hello world\nfrom OCR", paragraph.SourceText);
        Assert.Equal(new OcrBounds(10, 10, 110, 44), paragraph.Bounds);
    }

    [Fact]
    public void Aggregate_SplitsDistantParagraphsAndIsDeterministicForUnorderedInput()
    {
        var ordered = new[]
        {
            Block("b0001", "first", 0, 0),
            Block("b0002", "second", 0, 60)
        };
        var shuffled = ordered.Reverse().ToArray();

        var first = OcrBlockAggregator.Aggregate(ordered);
        var second = OcrBlockAggregator.Aggregate(shuffled);

        Assert.Equal(2, first.Count);
        Assert.Equal(new[] { "p0001", "p0002" }, first.Select(paragraph => paragraph.ParagraphId));
        Assert.Equal(
            first.Select(paragraph => (paragraph.ParagraphId, paragraph.SourceText, paragraph.Bounds)),
            second.Select(paragraph => (paragraph.ParagraphId, paragraph.SourceText, paragraph.Bounds)));
    }

    private static OcrTextBlock Block(string id, string text, int x, int y) =>
        new(id, text, new OcrBounds(x, y, text.Length * 10, 20));
}
