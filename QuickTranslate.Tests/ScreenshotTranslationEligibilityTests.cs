using QuickTranslate.Core;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ScreenshotTranslationEligibilityTests
{
    [Theory]
    [InlineData("", ScreenshotTranslationEligibilityReason.EmptyText)]
    [InlineData("   \n\t", ScreenshotTranslationEligibilityReason.EmptyText)]
    [InlineData("123 !!!", ScreenshotTranslationEligibilityReason.NonText)]
    public void Evaluate_SkipsTextThatCannotProduceTranslation(string text, ScreenshotTranslationEligibilityReason reason)
    {
        var result = ScreenshotTranslationEligibilityEvaluator.Evaluate(text, "简体中文");

        Assert.Equal(ScreenshotTranslationAction.Skip, result.Action);
        Assert.Equal(reason, result.Reason);
    }

    [Theory]
    [InlineData("https://example.com/docs?q=1")]
    [InlineData("www.example.org/path")]
    [InlineData("example.com")]
    public void Evaluate_PreservesUrls(string text)
    {
        var result = ScreenshotTranslationEligibilityEvaluator.Evaluate(text, "简体中文");

        Assert.Equal(ScreenshotTranslationAction.Preserve, result.Action);
        Assert.Equal(ScreenshotTranslationEligibilityReason.Url, result.Reason);
    }

    [Theory]
    [InlineData("C:\\Projects\\QuickTranslate\\App.xaml.cs")]
    [InlineData("/usr/local/bin/python")]
    [InlineData("../docs/README.md")]
    [InlineData("report.pdf")]
    public void Evaluate_PreservesFilePaths(string text)
    {
        var result = ScreenshotTranslationEligibilityEvaluator.Evaluate(text, "简体中文");

        Assert.Equal(ScreenshotTranslationAction.Preserve, result.Action);
        Assert.Equal(ScreenshotTranslationEligibilityReason.FilePath, result.Reason);
    }

    [Theory]
    [InlineData("public async Task TranslateAsync() { return; }")]
    [InlineData("git status --short")]
    [InlineData("SELECT id, name FROM users WHERE active = 1;")]
    public void Evaluate_PreservesCodeAndCommands(string text)
    {
        var result = ScreenshotTranslationEligibilityEvaluator.Evaluate(text, "简体中文");

        Assert.Equal(ScreenshotTranslationAction.Preserve, result.Action);
        Assert.Equal(ScreenshotTranslationEligibilityReason.Code, result.Reason);
        Assert.Equal(ContentType.Code, result.DetectedContentType);
    }

    [Theory]
    [InlineData("API_KEY")]
    [InlineData("camelCaseValue")]
    [InlineData("model_v6_small")]
    [InlineData("std::vector")]
    public void Evaluate_PreservesIdentifiers(string text)
    {
        var result = ScreenshotTranslationEligibilityEvaluator.Evaluate(text, "简体中文");

        Assert.Equal(ScreenshotTranslationAction.Preserve, result.Action);
        Assert.Equal(ScreenshotTranslationEligibilityReason.Identifier, result.Reason);
    }

    [Theory]
    [InlineData("这是一段中文正文，用于确认不会重复翻译。", "简体中文", SourceLanguageFamily.Han)]
    [InlineData("これは日本語の文章です。", "日本語", SourceLanguageFamily.Japanese)]
    [InlineData("This is an English sentence for the reader.", "English", SourceLanguageFamily.Latin)]
    public void Evaluate_PreservesTextAlreadyInTargetLanguage(
        string text,
        string targetLanguage,
        SourceLanguageFamily sourceFamily)
    {
        var result = ScreenshotTranslationEligibilityEvaluator.Evaluate(text, targetLanguage);

        Assert.Equal(ScreenshotTranslationAction.Preserve, result.Action);
        Assert.Equal(ScreenshotTranslationEligibilityReason.TargetLanguageMatch, result.Reason);
        Assert.Equal(sourceFamily, result.SourceLanguageFamily);
    }

    [Theory]
    [InlineData("This is an English sentence.", "简体中文")]
    [InlineData("这是一段中文正文，需要翻译为英文。", "English")]
    [InlineData("これは日本語の文章です。", "简体中文")]
    public void EvaluateTranslatesNaturalLanguageWhenTargetDiffers(string text, string targetLanguage)
    {
        var result = ScreenshotTranslationEligibilityEvaluator.Evaluate(text, targetLanguage);

        Assert.Equal(ScreenshotTranslationAction.Translate, result.Action);
        Assert.Equal(ScreenshotTranslationEligibilityReason.NaturalLanguage, result.Reason);
    }

    [Fact]
    public void Evaluate_DoesNotTreatOrdinarySentenceAsIdentifier()
    {
        var result = ScreenshotTranslationEligibilityEvaluator.Evaluate(
            "Please check the configuration before continuing.",
            "简体中文");

        Assert.Equal(ScreenshotTranslationAction.Translate, result.Action);
        Assert.NotEqual(ScreenshotTranslationEligibilityReason.Identifier, result.Reason);
    }
}
