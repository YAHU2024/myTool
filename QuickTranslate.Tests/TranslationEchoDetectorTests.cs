using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public class TranslationEchoDetectorTests
{
    private const string LongEnglishSource =
        "TranslationEchoDetector conservatively detects when a language model returns the input " +
        "text back to the user instead of translating it into the requested target language, " +
        "without rejecting legitimate technical translations.";

    [Fact]
    public void Detect_VerbatimNaturalLanguage_IsConfirmed()
    {
        var result = TranslationEchoDetector.Detect(LongEnglishSource, LongEnglishSource);

        Assert.Equal(TranslationEchoConfidence.Confirmed, result.Confidence);
        Assert.Equal("normalized_equal", result.Reason);
    }

    [Fact]
    public void Detect_CaseAndWhitespaceChanges_AreConfirmed()
    {
        var echo = LongEnglishSource.ToUpperInvariant().Replace(" INPUT ", "\r\nINPUT ", StringComparison.Ordinal);

        Assert.True(TranslationEchoDetector.Detect(LongEnglishSource, echo).IsConfirmed);
    }

    [Fact]
    public void Detect_SmallEditInLongEcho_IsAtLeastSuspected()
    {
        var echo = LongEnglishSource.Replace("requested", "specified", StringComparison.Ordinal);

        Assert.NotEqual(
            TranslationEchoConfidence.None,
            TranslationEchoDetector.Detect(LongEnglishSource, echo).Confidence);
    }

    [Fact]
    public void Detect_RealTranslation_IsNotEcho()
    {
        const string translation =
            "TranslationEchoDetector 会以保守方式检测语言模型是否直接返回输入内容，而不是完成目标语言翻译，同时避免拒绝合法的技术文本译文。";

        Assert.Equal(
            TranslationEchoConfidence.None,
            TranslationEchoDetector.Detect(LongEnglishSource, translation).Confidence);
    }

    [Fact]
    public void Detect_UnrelatedText_IsNotEcho()
    {
        const string unrelated =
            "The quick brown fox jumps over the lazy dog while the morning sun rises over a quiet harbor and fishing boats return home.";

        Assert.Equal(
            TranslationEchoConfidence.None,
            TranslationEchoDetector.Detect(LongEnglishSource, unrelated).Confidence);
    }

    [Theory]
    [InlineData("Playwright")]
    [InlineData("43.59")]
    [InlineData("Kubernetes")]
    public void Detect_ShortUnchangedInput_IsNotEcho(string input)
    {
        Assert.Equal(
            TranslationEchoConfidence.None,
            TranslationEchoDetector.Detect(input, input).Confidence);
    }

    [Fact]
    public void Detect_LongUrl_IsOnlySuspected()
    {
        const string url = "https://www.example.com/a/very/long/path/to/a/resource?version=2026&language=en";

        Assert.Equal(
            TranslationEchoConfidence.Suspected,
            TranslationEchoDetector.Detect(url, url).Confidence);
    }

    [Fact]
    public void Detect_PathHeavyLegitimateTranslation_IsNotConfirmed()
    {
        var paths = string.Join('\n', Enumerable.Range(1, 20)
            .Select(index => $"QuickTranslate/Services/Component{index}.cs"));
        var source = $"The following files implement the translation pipeline and must be reviewed carefully.\n{paths}";
        var translation = $"以下文件实现了翻译处理流程，需要仔细检查。\n{paths}";

        Assert.NotEqual(
            TranslationEchoConfidence.Confirmed,
            TranslationEchoDetector.Detect(source, translation).Confidence);
    }

    [Fact]
    public void Detect_CjkVerbatimNaturalLanguage_IsConfirmed()
    {
        const string source =
            "这是一个用于测试回显检测的中文段落，包含足够长的自然语言内容，以确认模型将输入原样返回时能够被保守而准确地识别出来。";

        Assert.True(TranslationEchoDetector.Detect(source, source).IsConfirmed);
    }
}
