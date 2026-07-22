using System.Windows;
using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

public class FloatingWindowPlacementTests
{
    [Fact]
    public void ShouldPlaceAbove_UsesSideWithMoreSpace()
    {
        var workArea = new Rect(-1920, 0, 1920, 1080);

        Assert.False(FloatingWindowPlacement.ShouldPlaceAbove(
            new Point(-1000, 200), workArea, 8));
        Assert.True(FloatingWindowPlacement.ShouldPlaceAbove(
            new Point(-1000, 900), workArea, 8));
    }

    [Fact]
    public void Calculate_KeepsBelowPlacementAnchoredAndInsideWorkArea()
    {
        var workArea = new Rect(0, 0, 1920, 1080);

        var result = FloatingWindowPlacement.Calculate(
            new Point(1000, 300),
            new Size(420, 200),
            workArea,
            placeAbove: false,
            gap: 8);

        Assert.Equal(new Rect(790, 308, 420, 200), result);
    }

    [Fact]
    public void Calculate_ClampsAcrossNegativeMonitorEdges()
    {
        var workArea = new Rect(-1920, 0, 1920, 1080);

        var result = FloatingWindowPlacement.Calculate(
            new Point(-1900, 1000),
            new Size(420, 300),
            workArea,
            placeAbove: true,
            gap: 12);

        Assert.Equal(-1920, result.Left);
        Assert.Equal(688, result.Top);
        Assert.True(result.Right <= workArea.Right);
        Assert.True(result.Bottom <= workArea.Bottom);
    }
}
