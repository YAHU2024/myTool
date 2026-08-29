using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class WindowsMediaOcrServiceTests
{
    [Fact]
    public void Probe_ReturnsStableCapabilityWithoutThrowing()
    {
        var service = new WindowsMediaOcrService();
        var capability = service.Probe();

        Assert.NotNull(capability);
        Assert.NotNull(capability.UnavailableReason);
        Assert.NotNull(capability.SupportedLanguageTags);
        Assert.Equal(
            capability.SupportedLanguageTags.Count,
            capability.SupportedLanguageTags
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.True(capability.MaxImageDimension is null or > 0);
    }
}
