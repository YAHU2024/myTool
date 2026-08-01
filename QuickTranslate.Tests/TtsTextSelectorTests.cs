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

    // ==================== Unicode scalar truncation (P2-3) ====================

    [Fact]
    public void NormalizeForSpeech_PreservesSurrogatePairs()
    {
        // 🎉 is U+1F389 = surrogate pair D83C DF89 (2 UTF-16 units, 1 rune).
        var raw = "abc🎉def";
        var text = TtsTextSelector.NormalizeForSpeech(raw, maxChars: 4, out var truncated);
        // 4 runes: 'a' 'b' 'c' '🎉'
        Assert.True(truncated);
        Assert.Equal(5, text.Length); // 3 BMP + 1 surrogate pair = 5 UTF-16 units
        Assert.Equal("abc🎉", text);
        // Verify no unpaired surrogates.
        Assert.False(char.IsSurrogate(text[^1]) && text.Length > 1 && !char.IsSurrogatePair(text[^2], text[^1]));
    }

    [Fact]
    public void NormalizeForSpeech_TruncatesByRuneNotUtf16()
    {
        // 5 BMP chars: each is 1 UTF-16 unit = 1 rune.
        var text = TtsTextSelector.NormalizeForSpeech("12345abc", maxChars: 3, out var truncated);
        Assert.True(truncated);
        Assert.Equal("123", text);
        Assert.Equal(3, text.Length);
    }

    [Fact]
    public void NormalizeForSpeech_NoTruncationWhenUnderLimit()
    {
        var text = TtsTextSelector.NormalizeForSpeech("hello🎉", maxChars: 100, out var truncated);
        Assert.False(truncated);
        Assert.Equal("hello🎉", text);
    }

    [Fact]
    public void NormalizeForSpeech_ChineseAndEmojiMixed()
    {
        // "中国🎉测试" = 2 + 1 + 2 = 5 runes, 6 UTF-16 units.
        var raw = "中国🎉测试";
        var text = TtsTextSelector.NormalizeForSpeech(raw, maxChars: 4, out var truncated);
        Assert.True(truncated);
        Assert.Equal("中国🎉测", text);
        Assert.False(char.IsSurrogate(text[^1])); // does not end with unpaired surrogate
    }

    [Fact]
    public void NormalizeForSpeech_TruncationExactlyOnEmojiBoundary()
    {
        // "a🎉b" = 3 runes. maxChars=2 → "a🎉" (3 UTF-16 units).
        var text = TtsTextSelector.NormalizeForSpeech("a🎉b", maxChars: 2, out var truncated);
        Assert.True(truncated);
        Assert.Equal("a🎉", text);
    }

    [Fact]
    public void TryTruncateByRuneCount_ReturnsOriginalWhenUnderLimit()
    {
        Assert.False(TtsTextSelector.TryTruncateByRuneCount("hello", 10, out var result));
        Assert.Equal("hello", result);
    }

    [Fact]
    public void TryTruncateByRuneCount_PreservesSurrogatePairs()
    {
        Assert.True(TtsTextSelector.TryTruncateByRuneCount("a🎉b🎉c", 3, out var result));
        Assert.Equal("a🎉b", result);
        // 1 BMP + 1 surrogate pair + 1 BMP = 4 UTF-16 units.
        Assert.Equal(4, result.Length);
    }

    [Fact]
    public void TryTruncateByRuneCount_ReturnsFalseForEmptyOrZero()
    {
        Assert.False(TtsTextSelector.TryTruncateByRuneCount("", 5, out _));
        Assert.False(TtsTextSelector.TryTruncateByRuneCount("abc", 0, out var r));
        Assert.Equal("abc", r);
    }
}
