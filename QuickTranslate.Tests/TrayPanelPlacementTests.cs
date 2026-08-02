using QuickTranslate.Core;
using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class TrayPanelPlacementTests
{
    [Theory]
    [InlineData(96, 12)]
    [InlineData(120, 15)]
    [InlineData(144, 18)]
    [InlineData(192, 24)]
    public void Calculate_AppliesDipMarginAtMonitorDpi(double dpi, int expectedMargin)
    {
        var workArea = new PhysicalRect(0, 0, 1920, 1040);

        var result = TrayPanelPlacement.Calculate(
            workArea,
            new PhysicalPoint(1900, 1000),
            new PhysicalSize(420, 600),
            dpi,
            dpi);

        Assert.Equal(workArea.Right - expectedMargin, result.Right);
        Assert.Equal(workArea.Bottom - expectedMargin, result.Bottom);
    }

    [Fact]
    public void Calculate_ClampsToNegativeCoordinateWorkArea()
    {
        var workArea = new PhysicalRect(-1920, -100, 1920, 1080);

        var result = TrayPanelPlacement.Calculate(
            workArea,
            new PhysicalPoint(-1910, 900),
            new PhysicalSize(420, 600),
            144,
            144);

        Assert.True(result.Left >= workArea.Left);
        Assert.True(result.Top >= workArea.Top);
        Assert.True(result.Right <= workArea.Right);
        Assert.True(result.Bottom <= workArea.Bottom);
    }

    [Fact]
    public void Calculate_OversizedPanelPinsToWorkAreaOrigin()
    {
        var workArea = new PhysicalRect(100, 200, 300, 400);

        var result = TrayPanelPlacement.Calculate(
            workArea,
            new PhysicalPoint(200, 300),
            new PhysicalSize(500, 600),
            96,
            96);

        Assert.Equal(100, result.Left);
        Assert.Equal(200, result.Top);
    }
}
