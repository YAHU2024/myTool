using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class OcrLanguageSelectionTests
{
    [Fact]
    public void Select_UsesExactLanguageWhenAvailable()
    {
        var result = OcrLanguageSelector.Select(
            new[] { "en-US", "zh-Hans-CN" },
            "zh-Hans-CN",
            allowFallback: true);

        Assert.True(result.IsAvailable);
        Assert.False(result.FallbackUsed);
        Assert.Equal("zh-Hans-CN", result.SelectedLanguageTag);
    }

    [Fact]
    public void Select_UsesSameBaseLanguageAsSafeMatch()
    {
        var result = OcrLanguageSelector.Select(
            new[] { "zh-Hans-CN" },
            "zh-Hans",
            allowFallback: false);

        Assert.True(result.IsAvailable);
        Assert.False(result.FallbackUsed);
        Assert.Equal("zh-Hans-CN", result.SelectedLanguageTag);
    }

    [Fact]
    public void Select_DoesNotTreatDifferentChineseScriptsAsTheSameLanguage()
    {
        var result = OcrLanguageSelector.Select(
            new[] { "zh-Hant-TW" },
            "zh-Hans",
            allowFallback: false);

        Assert.False(result.IsAvailable);
        Assert.Null(result.SelectedLanguageTag);
    }

    [Fact]
    public void Select_RejectsUnavailableLanguageWhenFallbackIsForbidden()
    {
        var result = OcrLanguageSelector.Select(
            new[] { "zh-Hans-CN" },
            "en-US",
            allowFallback: false);

        Assert.False(result.IsAvailable);
        Assert.Null(result.SelectedLanguageTag);
        Assert.Equal("requested_language_unavailable", result.Reason);
    }

    [Fact]
    public void Select_ReportsFallbackAndPrefersUserProfileLanguage()
    {
        var result = OcrLanguageSelector.Select(
            new[] { "zh-Hans-CN", "en-US" },
            "ja-JP",
            allowFallback: true,
            userProfileLanguageTag: "en-US");

        Assert.True(result.IsAvailable);
        Assert.True(result.FallbackUsed);
        Assert.Equal("en-US", result.SelectedLanguageTag);
    }
}
