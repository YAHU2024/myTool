using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ScreenshotSelectionTests
{
    private static readonly ScreenshotRegion Monitor = new(-1920, -100, 1920, 1080);

    [Fact]
    public void Evaluate_AcceptsReversedDragWithinMonitor()
    {
        var result = ScreenshotSelectionModel.Evaluate(
            new PhysicalPoint(-120, 420),
            new PhysicalPoint(-620, 80),
            Monitor);

        Assert.True(result.IsAccepted);
        Assert.Equal(new ScreenshotRegion(-620, 80, 500, 340), result.Region);
        Assert.Equal(ScreenshotSelectionRejection.None, result.Rejection);
    }

    [Fact]
    public void Evaluate_RejectsTooSmallSelection()
    {
        var result = ScreenshotSelectionModel.Evaluate(
            new PhysicalPoint(-10, 10),
            new PhysicalPoint(13, 33),
            new ScreenshotRegion(-100, -100, 500, 500));

        Assert.False(result.IsAccepted);
        Assert.Equal(ScreenshotSelectionRejection.TooSmall, result.Rejection);
    }

    [Fact]
    public void Evaluate_RejectsEndpointOutsideMonitor()
    {
        var result = ScreenshotSelectionModel.Evaluate(
            new PhysicalPoint(-10, 10),
            new PhysicalPoint(50, 50),
            new ScreenshotRegion(0, 0, 500, 500));

        Assert.False(result.IsAccepted);
        Assert.Equal(ScreenshotSelectionRejection.StartOutsideMonitor, result.Rejection);
    }

    [Fact]
    public void Evaluate_RejectsResourceLimitBeforeCapture()
    {
        var result = ScreenshotSelectionModel.Evaluate(
            new PhysicalPoint(10, 10),
            new PhysicalPoint(110, 110),
            new ScreenshotRegion(0, 0, 500, 500),
            resourceLimits: new OcrResourceLimits(MaxPixelCount: 5_000));

        Assert.False(result.IsAccepted);
        Assert.Equal(ScreenshotSelectionRejection.ExceedsResourceLimit, result.Rejection);
    }

    [Fact]
    public void Evaluate_AcceptsNegativeCoordinateMonitor()
    {
        var result = ScreenshotSelectionModel.Evaluate(
            new PhysicalPoint(-1919, -99),
            new PhysicalPoint(-1800, 0),
            Monitor);

        Assert.True(result.IsAccepted);
        Assert.True(Monitor.Contains(result.Region));
    }

    [Fact]
    public void ScreenshotRegion_ContainsUsesHalfOpenPhysicalBounds()
    {
        var region = new ScreenshotRegion(-10, -20, 30, 40);

        Assert.True(region.Contains(new PhysicalPoint(-10, -20)));
        Assert.True(region.Contains(new PhysicalPoint(19, 19)));
        Assert.False(region.Contains(new PhysicalPoint(20, 19)));
        Assert.False(region.Contains(new PhysicalPoint(19, 20)));
    }

    [Fact]
    public void ScreenshotRegion_ContainsHandlesExtremeCoordinatesWithoutOverflow()
    {
        var region = new ScreenshotRegion(int.MaxValue - 10, int.MinValue + 10, 10, 10);

        Assert.True(region.Contains(new PhysicalPoint(int.MaxValue - 1, int.MinValue + 18)));
        Assert.False(region.Contains(new PhysicalPoint(int.MaxValue, int.MinValue + 18)));
    }

    [Fact]
    public void GdiCapture_RejectsResourceLimitBeforeTouchingDesktop()
    {
        var service = new GdiScreenshotCaptureService(
            new OcrResourceLimits(MaxPixelCount: 100));

        Assert.Throws<ArgumentException>(() =>
            service.Capture(new ScreenshotRegion(0, 0, 11, 11)));
    }
}
