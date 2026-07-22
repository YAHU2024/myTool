using System.Windows;
using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

public class FloatingWindowPlacementTests
{
    [Fact]
    public void ShouldPlaceAbove_PrefersBelowWhenWindowFits()
    {
        var workArea = new Rect(0, 0, 1920, 1080);
        var selection = new Rect(900, 540, 100, 24);

        var placeAbove = FloatingWindowPlacement.ShouldPlaceAbove(
            selection,
            workArea,
            requiredHeight: 200,
            gap: 8);

        Assert.False(placeAbove);
    }

    [Fact]
    public void ShouldPlaceAbove_UsesAboveOnlyWhenBelowDoesNotFit()
    {
        var workArea = new Rect(0, 0, 1920, 1080);
        var selection = new Rect(900, 950, 100, 24);

        var placeAbove = FloatingWindowPlacement.ShouldPlaceAbove(
            selection,
            workArea,
            requiredHeight: 200,
            gap: 8);

        Assert.True(placeAbove);
    }

    [Fact]
    public void Calculate_BelowDoesNotIntersectSingleLineSelection()
    {
        var workArea = new Rect(0, 0, 1920, 1080);
        var selection = new Rect(900, 540, 100, 24);

        var result = FloatingWindowPlacement.Calculate(
            new Point(1000, 540),
            selection,
            new Size(420, 200),
            workArea,
            placeAbove: false,
            gap: 8);

        Assert.Equal(new Rect(790, 572, 420, 200), result);
        Assert.False(result.IntersectsWith(selection));
    }

    [Fact]
    public void Calculate_AboveDoesNotIntersectMultilineSelection()
    {
        var workArea = new Rect(0, 0, 1920, 1080);
        var selection = new Rect(800, 500, 200, 220);

        var result = FloatingWindowPlacement.Calculate(
            new Point(1000, 700),
            selection,
            new Size(420, 300),
            workArea,
            placeAbove: true,
            gap: 8);

        Assert.Equal(new Rect(790, 192, 420, 300), result);
        Assert.False(result.IntersectsWith(selection));
    }

    [Fact]
    public void Calculate_ClampsAcrossNegativeMonitorEdges()
    {
        var workArea = new Rect(-1920, 0, 1920, 1080);
        var selection = new Rect(-1920, 900, 40, 24);

        var result = FloatingWindowPlacement.Calculate(
            new Point(-1900, 900),
            selection,
            new Size(420, 300),
            workArea,
            placeAbove: true,
            gap: 12);

        Assert.Equal(-1920, result.Left);
        Assert.Equal(588, result.Top);
        Assert.False(result.IntersectsWith(selection));
        Assert.True(result.Right <= workArea.Right);
        Assert.True(result.Bottom <= workArea.Bottom);
    }
}
