using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class TtsTextSelectorTests
{
    [Fact]
    public void CanSpeak_RequiresCompletedNonEmptyAndEnabled()
    {
        Assert.True(TtsTextSelector.CanSpeak(ModeResultStatus.Completed, "hello", true));
        Assert.False(TtsTextSelector.CanSpeak(ModeResultStatus.Loading, "hello", true));
        Assert.False(TtsTextSelector.CanSpeak(ModeResultStatus.Completed, "  ", true));
        Assert.False(TtsTextSelector.CanSpeak(ModeResultStatus.Completed, "hello", false));
    }

    [Fact]
    public void NormalizeForSpeech_StripsFenceLinesAndTruncates()
    {
        var raw = "line1\n```csharp\ncode\n```\nline2";
        var text = TtsTextSelector.NormalizeForSpeech(raw, maxChars: 8, out var truncated);
        Assert.DoesNotContain("```", text);
        Assert.True(truncated);
        Assert.Equal(8, text.Length);
    }

    [Theory]
    [InlineData("这是一段中文内容", "zh")]
    [InlineData("This is English only text", "en")]
    public void DetectLanguageHint_UsesCjkRatio(string text, string expected) =>
        Assert.Equal(expected, TtsTextSelector.DetectLanguageHint(text));

    [Fact]
    public void ResolveVoice_UsesOverrideOrAuto()
    {
        Assert.Equal(TtsTextSelector.VoiceGuy, TtsTextSelector.ResolveVoice("中文", TtsTextSelector.VoiceGuy));
        Assert.Equal(TtsTextSelector.VoiceXiaoxiao, TtsTextSelector.ResolveVoice("中文内容", null));
        Assert.Equal(TtsTextSelector.VoiceJenny, TtsTextSelector.ResolveVoice("English only", ""));
    }

    [Fact]
    public void CreateSpeakPlan_AutoPicksByLanguage()
    {
        var zh = TtsTextSelector.CreateSpeakPlan("中文结果", voiceOverride: null, rate: 1.0, maxChars: 2000);
        Assert.Equal(TtsTextSelector.SelectionAuto, zh.SelectionMode);
        Assert.Equal(TtsTextSelector.VoiceSourceAuto, zh.VoiceSource);
        Assert.Equal(TtsTextSelector.VoiceXiaoxiao, zh.Voice);
        Assert.Equal("zh", zh.LanguageHint);
        Assert.Null(zh.FallbackFrom);

        var en = TtsTextSelector.CreateSpeakPlan("English only result", null, 1.1, 2000);
        Assert.Equal(TtsTextSelector.VoiceJenny, en.Voice);
        Assert.Equal("en", en.LanguageHint);
        Assert.Equal(1.1, en.Rate);
    }

    [Fact]
    public void CreateSpeakPlan_ManualKeepsUserVoiceEvenWhenLanguageMismatches()
    {
        var plan = TtsTextSelector.CreateSpeakPlan(
            "这是中文",
            voiceOverride: TtsTextSelector.VoiceJenny,
            rate: 1.0,
            maxChars: 2000);

        Assert.Equal(TtsTextSelector.SelectionManual, plan.SelectionMode);
        Assert.Equal(TtsTextSelector.VoiceSourceUser, plan.VoiceSource);
        Assert.Equal(TtsTextSelector.VoiceJenny, plan.Voice);
        Assert.Equal("zh", plan.LanguageHint);
        Assert.Null(plan.FallbackFrom);
    }

    [Fact]
    public void WithFallbackVoice_MarksFallbackSource()
    {
        var plan = TtsTextSelector.CreateSpeakPlan("中文", TtsTextSelector.VoiceYunxi, 1.0, 0);
        var fallback = TtsTextSelector.WithFallbackVoice(plan, TtsTextSelector.VoiceXiaoxiao);
        Assert.Equal(TtsTextSelector.VoiceXiaoxiao, fallback.Voice);
        Assert.Equal(TtsTextSelector.VoiceSourceFallback, fallback.VoiceSource);
        Assert.Equal(TtsTextSelector.VoiceYunxi, fallback.FallbackFrom);
        Assert.Equal(TtsTextSelector.SelectionManual, fallback.SelectionMode);
    }

    [Fact]
    public void BuildSsml_UsesVoiceLocale()
    {
        var zh = TtsTextSelector.BuildSsml(TtsTextSelector.VoiceXiaoxiao, "你好", 1.0);
        Assert.Contains("xml:lang='zh-CN'", zh);
        Assert.Contains($"name='{TtsTextSelector.VoiceXiaoxiao}'", zh);

        var en = TtsTextSelector.BuildSsml(TtsTextSelector.VoiceJenny, TtsTextSelector.EscapeSsml("a<b"), 1.0);
        Assert.Contains("xml:lang='en-US'", en);
        Assert.Contains("rate='+0%'", en);
        Assert.Contains("a&lt;b", en);
    }

    [Fact]
    public void FormatProsodyRate_AndEscapeSsml()
    {
        Assert.Equal("+0%", TtsTextSelector.FormatProsodyRate(1.0));
        Assert.Equal("-10%", TtsTextSelector.FormatProsodyRate(0.9));
        Assert.Equal("+10%", TtsTextSelector.FormatProsodyRate(1.1));
        Assert.Equal("&amp;&lt;&gt;&quot;&apos;", TtsTextSelector.EscapeSsml("&<>\"'"));
    }

    [Fact]
    public void LocaleFromVoice_AndIsChineseVoice()
    {
        Assert.Equal("zh-CN", TtsTextSelector.LocaleFromVoice(TtsTextSelector.VoiceYunxi));
        Assert.Equal("en-US", TtsTextSelector.LocaleFromVoice(TtsTextSelector.VoiceGuy));
        Assert.True(TtsTextSelector.IsChineseVoice(TtsTextSelector.VoiceXiaoxiao));
        Assert.False(TtsTextSelector.IsChineseVoice(TtsTextSelector.VoiceJenny));
    }
}
