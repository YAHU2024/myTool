using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public class OpenAITranslationServicePromptTests
{
    [Fact]
    public void BuildSystemPrompt_SmartTranslation_UsesShortCodeFallback()
    {
        var service = CreateService(new AppSettings
        {
            SmartContentType = true,
            AutoDetectLanguage = false
        });

        var prompt = service.BuildSystemPrompt("English", ContentType.Translation, "bonjour");

        Assert.Contains("If the input is code, explain it briefly in English instead.", prompt);
        Assert.DoesNotContain("Exception: if the input is clearly code or a shell command", prompt);
    }

    [Theory]
    [InlineData(ContentType.Code, "code and command explanation assistant")]
    [InlineData(ContentType.Term, "knowledge assistant")]
    public void BuildSystemPrompt_SmartContent_TakesPrecedenceOverCustomTranslationPrompt(
        ContentType contentType,
        string expectedPromptText)
    {
        var service = CreateService(new AppSettings
        {
            SmartContentType = true,
            AutoDetectLanguage = true,
            FallbackLanguage = "French",
            CustomTranslationPrompt = "CUSTOM {targetLang}"
        });
        var fallbackUsed = false;

        var prompt = service.BuildSystemPrompt(
            "English",
            contentType,
            "English source",
            () => fallbackUsed = true);

        Assert.Contains(expectedPromptText, prompt);
        Assert.DoesNotContain("CUSTOM", prompt);
        Assert.Contains("English", prompt);
        Assert.False(fallbackUsed);
    }

    [Fact]
    public void BuildSystemPrompt_Translation_UsesCustomPromptBeforeSmartDefault()
    {
        var service = CreateService(new AppSettings
        {
            SmartContentType = true,
            AutoDetectLanguage = false,
            CustomTranslationPrompt = "Translate carefully to {targetLang}."
        });

        var prompt = service.BuildSystemPrompt("English", ContentType.Translation, "bonjour");

        Assert.Equal("Translate carefully to English.", prompt);
        Assert.DoesNotContain("If the input is code", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_TranslationMatchingTarget_UsesFallbackAndNotifiesCaller()
    {
        var service = CreateService(new AppSettings
        {
            SmartContentType = true,
            AutoDetectLanguage = true,
            FallbackLanguage = "French"
        });
        var fallbackUsed = false;

        var prompt = service.BuildSystemPrompt(
            "English",
            ContentType.Translation,
            "Already English",
            () => fallbackUsed = true);

        Assert.Contains("Translate the input to French.", prompt);
        Assert.Contains("explain it briefly in English instead.", prompt);
        Assert.True(fallbackUsed);
    }

    [Fact]
    public void BuildSystemPrompt_AutoDetectionDisabled_KeepsExplicitFallbackInstruction()
    {
        var service = CreateService(new AppSettings
        {
            SmartContentType = false,
            AutoDetectLanguage = false,
            FallbackLanguage = "French"
        });

        var prompt = service.BuildSystemPrompt("English", ContentType.Translation, "Already English");

        Assert.Contains("Translate the input to English.", prompt);
        Assert.Contains("If already in English, translate to French.", prompt);
    }

    private static OpenAITranslationService CreateService(AppSettings settings)
    {
        return new OpenAITranslationService(settings);
    }
}
