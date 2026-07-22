using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public class AutoScrollControllerTests
{
    [Fact]
    public void BeginRequest_EnablesFollowingAndRequestsScrollToEnd()
    {
        var controller = new AutoScrollController();
        controller.PauseForUpwardNavigation();

        var shouldScrollToEnd = controller.BeginRequest();

        Assert.True(controller.IsAutoScrollEnabled);
        Assert.True(shouldScrollToEnd);
    }

    [Fact]
    public void ContentGrowth_KeepsFollowingEnabledUntilUserBrowsesAway()
    {
        var controller = new AutoScrollController();

        var shouldScrollToEnd = controller.OnContentOrViewportChanged();

        Assert.True(controller.IsAutoScrollEnabled);
        Assert.True(shouldScrollToEnd);
    }

    [Theory]
    [InlineData(80, 100, 200, true)]
    [InlineData(79.99, 100, 200, false)]
    [InlineData(100, 100, 200, true)]
    public void UserScrollPosition_UsesTwentyDipBottomThreshold(
        double offset,
        double viewport,
        double extent,
        bool expected)
    {
        var controller = new AutoScrollController();

        var shouldScrollToEnd = controller.OnUserScrollPositionChanged(offset, viewport, extent);

        Assert.Equal(expected, controller.IsAutoScrollEnabled);
        Assert.Equal(expected, shouldScrollToEnd);
    }

    [Fact]
    public void UpwardNavigation_PausesFollowingUntilUserReturnsToBottom()
    {
        var controller = new AutoScrollController();

        controller.PauseForUpwardNavigation();
        var shouldScrollDuringAppend = controller.OnContentOrViewportChanged();
        var shouldScrollAtBottom = controller.OnUserScrollPositionChanged(80, 100, 200);

        Assert.False(shouldScrollDuringAppend);
        Assert.True(controller.IsAutoScrollEnabled);
        Assert.True(shouldScrollAtBottom);
    }

    [Fact]
    public void Resume_EnablesFollowingAndRequestsScrollToEnd()
    {
        var controller = new AutoScrollController();
        controller.PauseForUpwardNavigation();

        var shouldScrollToEnd = controller.Resume();

        Assert.True(controller.IsAutoScrollEnabled);
        Assert.True(shouldScrollToEnd);
    }

    [Theory]
    [InlineData(double.NaN, 100, 200)]
    [InlineData(80, double.PositiveInfinity, 200)]
    public void InvalidMetrics_AreNotTreatedAsBottom(double offset, double viewport, double extent)
    {
        Assert.False(AutoScrollController.IsNearBottom(offset, viewport, extent));
    }
}
