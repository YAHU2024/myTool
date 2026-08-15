using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

public class FloatingStatusMessageTests
{
    [Theory]
    [InlineData(FloatingStatusKind.Info)]
    [InlineData(FloatingStatusKind.Success)]
    [InlineData(FloatingStatusKind.Warning)]
    [InlineData(FloatingStatusKind.Error)]
    public void GetAccentColors_ReturnsDistinctOpaquePair(FloatingStatusKind kind)
    {
        var (indicator, fg) = FloatingStatusMessage.GetAccentColors(kind);
        Assert.NotEqual(indicator, fg);
        Assert.True(indicator.A == 255);
        Assert.True(fg.A == 255);
    }

    [Fact]
    public void ResolveDuration_UsesKindDefaultsWhenNull()
    {
        Assert.Equal(FloatingStatusMessage.SuccessDuration, FloatingStatusMessage.ResolveDuration(FloatingStatusKind.Success, null));
        Assert.Equal(FloatingStatusMessage.WarningDuration, FloatingStatusMessage.ResolveDuration(FloatingStatusKind.Warning, null));
        Assert.Equal(FloatingStatusMessage.DefaultTransientDuration, FloatingStatusMessage.ResolveDuration(FloatingStatusKind.Error, null));
        Assert.Equal(TimeSpan.FromSeconds(2), FloatingStatusMessage.ResolveDuration(FloatingStatusKind.Error, TimeSpan.FromSeconds(2)));
    }
}
