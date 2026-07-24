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
    public void FormatProsodyRate_AndEscapeSsml()
    {
        Assert.Equal("+0%", TtsTextSelector.FormatProsodyRate(1.0));
        Assert.Equal("-10%", TtsTextSelector.FormatProsodyRate(0.9));
        Assert.Equal("+10%", TtsTextSelector.FormatProsodyRate(1.1));
        Assert.Equal("&amp;&lt;&gt;&quot;&apos;", TtsTextSelector.EscapeSsml("&<>\"'"));
        var ssml = TtsTextSelector.BuildSsml(TtsTextSelector.VoiceJenny, TtsTextSelector.EscapeSsml("a<b"), 1.0);
        Assert.Contains("rate='+0%'", ssml);
        Assert.Contains("a&lt;b", ssml);
    }
}
