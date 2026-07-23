using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public class OpenAITranslationServicePromptTests
{
    [Fact]
    public void BuildSystemPrompt_Translation_RemainsTranslationWhenSmartDetectionIsEnabled()
    {
        var service = CreateService(new AppSettings
        {
            SmartContentType = true,
            AutoDetectLanguage = false
        });

        var prompt = service.BuildSystemPrompt("English", ContentType.Translation, "bonjour");

        Assert.Contains("Translate the input into English.", prompt);
        Assert.Contains("Always translate", prompt);
        Assert.Contains("Output only the translation", prompt);
        Assert.DoesNotContain("If the input is code", prompt);
    }

    [Theory]
    [InlineData(ContentType.Code, "terminal command")]
    [InlineData(ContentType.Term, "main use")]
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

        Assert.Contains("Translate the input into French.", prompt);
        Assert.DoesNotContain("If the input is code", prompt);
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

        Assert.Contains("Translate the input into English.", prompt);
        Assert.Contains("If it is already in English, translate it into French.", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_Code_PreservesCommandExplanationContractWithoutRepetition()
    {
        using var service = CreateService(new AppSettings());

        var prompt = service.BuildSystemPrompt("简体中文", ContentType.Code, "git reset --hard");

        Assert.Contains("code, script, SQL, configuration, or terminal command", prompt);
        Assert.Contains("option, pipe, redirect, and important side effect", prompt);
        Assert.Contains("Do not translate or reproduce the full source", prompt);
        Assert.Contains("no preamble, labels, or markdown headers", prompt);
        Assert.DoesNotContain("Do not output the source unchanged", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_Term_PreservesConciseExplanationContract()
    {
        using var service = CreateService(new AppSettings());

        var prompt = service.BuildSystemPrompt("简体中文", ContentType.Term, "dependency injection");

        Assert.Contains("1-2 concise sentences", prompt);
        Assert.Contains("what it is and its main use", prompt);
        Assert.Contains("Output only the explanation", prompt);
        Assert.DoesNotContain("Translate the input", prompt);
    }

    [Theory]
    [InlineData("general", "grammar, structure, and relevant context")]
    [InlineData("learner", "pronunciation when relevant")]
    [InlineData("literary", "imagery, symbolism, context, and style")]
    [InlineData("business", "industry terms, implications, and action items")]
    public void CreateRequest_AnalysisPreset_PreservesDistinctContract(
        string preset,
        string expectedFocus)
    {
        using var service = CreateService(new AppSettings
        {
            SelectedAnalysisPromptId = $"builtin:{preset}"
        });

        var request = service.CreateRequest(
            "Sample text",
            "简体中文",
            ContentType.Analysis,
            TranslationRequestKind.Analysis);

        Assert.Contains(expectedFocus, request.SystemPrompt);
        Assert.Contains("Output only a clear, concise analysis", request.SystemPrompt);
        Assert.DoesNotContain("Translate the input", request.SystemPrompt);
    }

    [Fact]
    public void CreateRequest_MissingCustomAnalysisProfile_UsesGeneralDefault()
    {
        using var service = CreateService(new AppSettings
        {
            AutoDetectLanguage = false,
            CustomTranslationPrompt = "   ",
            SelectedAnalysisPromptId = "custom:missing"
        });

        var translation = service.CreateRequest("bonjour", "English", ContentType.Translation);
        var analysis = service.CreateRequest(
            "bonjour",
            "English",
            ContentType.Analysis,
            TranslationRequestKind.Analysis);

        Assert.Contains("Translate the input into English", translation.SystemPrompt);
        Assert.Contains("grammar, structure, and relevant context", analysis.SystemPrompt);
    }

    private static OpenAITranslationService CreateService(AppSettings settings)
    {
        return new OpenAITranslationService(settings);
    }
}
