using Xunit;
using QuickTranslate.Core;
using QuickTranslate.Models;

namespace QuickTranslate.Tests;

public sealed class OcrReadingOrderSorterTests
{
    [Fact]
    public void SortsHorizontalBlocksByRowsThenColumns()
    {
        var result = OcrReadingOrderSorter.Sort(new[]
        {
            Block("b", 100, 0, 20, 20), Block("a", 0, 0, 20, 20), Block("c", 0, 40, 20, 20)
        });
        Assert.Equal(new[] { "a", "b", "c" }, result.Select(x => x.BlockId));
        Assert.Equal(new[] { 1, 2, 3 }, result.Select(x => x.ReadingOrder));
    }

    [Fact]
    public void SortsVerticalBlocksByColumnsThenRows()
    {
        var result = OcrReadingOrderSorter.Sort(new[]
        {
            Block("right", 100, 0, 20, 80), Block("left", 0, 50, 20, 80)
        });
        Assert.Equal(new[] { "left", "right" }, result.Select(x => x.BlockId));
    }

    [Fact]
    public void UsesExplicitRotationToClassifyVerticalText()
    {
        var result = OcrReadingOrderSorter.Sort(new[]
        {
            Block("rotated", 0, 0, 80, 20, 90), Block("horizontal", 0, 100, 80, 20)
        });
        Assert.Equal("horizontal", result[0].BlockId);
        Assert.Equal("rotated", result[1].BlockId);
    }

    private static OcrTextBlock Block(string id, int x, int y, int width, int height, double? angle = null) =>
        new(id, id, new OcrBounds(x, y, width, height), Polygon: null, OrientationDegrees: angle);
}

