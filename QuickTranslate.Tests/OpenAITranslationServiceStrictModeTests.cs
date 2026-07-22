using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class OpenAITranslationServiceStrictModeTests
{
    [Fact]
    public void CreateRequest_TranslationMode_DoesNotUseSmartCodeExplanationPrompt()
    {
        using var service = CreateService(new AppSettings
        {
            SmartContentType = true,
            AutoDetectLanguage = false
        });

        var request = service.CreateRequest("git reset --hard", "English", ContentType.Translation);

        Assert.Equal(TranslationRequestKind.Translation, request.Kind);
        Assert.Equal(ContentType.Translation, request.ContentType);
        Assert.Contains("You MUST always translate.", request.SystemPrompt);
        Assert.DoesNotContain("code and command explanation assistant", request.SystemPrompt);
        Assert.DoesNotContain("If the input is code", request.SystemPrompt);
    }

    [Fact]
    public void CreateRequest_CodeMode_UsesCodePromptRegardlessOfTranslationSettings()
    {
        using var service = CreateService(new AppSettings
        {
            SmartContentType = false,
            CustomTranslationPrompt = "CUSTOM {targetLang}"
        });

        var request = service.CreateRequest("Get-Process", "Simplified Chinese", ContentType.Code);

        Assert.Equal(TranslationRequestKind.Translation, request.Kind);
        Assert.Equal(ContentType.Code, request.ContentType);
        Assert.Contains("code and command explanation assistant", request.SystemPrompt);
        Assert.DoesNotContain("CUSTOM", request.SystemPrompt);
    }

    [Fact]
    public void CreateRequest_TermMode_UsesTermPromptRegardlessOfTranslationSettings()
    {
        using var service = CreateService(new AppSettings
        {
            SmartContentType = true,
            CustomTranslationPrompt = "CUSTOM {targetLang}"
        });

        var request = service.CreateRequest("dependency injection", "Simplified Chinese", ContentType.Term);

        Assert.Equal(TranslationRequestKind.Translation, request.Kind);
        Assert.Equal(ContentType.Term, request.ContentType);
        Assert.Contains("knowledge assistant", request.SystemPrompt);
        Assert.DoesNotContain("CUSTOM", request.SystemPrompt);
    }

    [Fact]
    public void CreateRequest_AnalysisMode_UsesAnalysisKindAndCustomAnalysisPrompt()
    {
        using var service = CreateService(new AppSettings
        {
            CustomTranslationPrompt = "TRANSLATION {targetLang}",
            CustomAnalysisPrompt = "ANALYSIS {targetLang}"
        });

        var request = service.CreateRequest(
            "A sample sentence.",
            "English",
            ContentType.Translation,
            TranslationRequestKind.Analysis);

        Assert.Equal(TranslationRequestKind.Analysis, request.Kind);
        Assert.Equal(ContentType.Analysis, request.ContentType);
        Assert.Equal("ANALYSIS English", request.SystemPrompt);
        Assert.DoesNotContain("TRANSLATION", request.SystemPrompt);
    }

    private static OpenAITranslationService CreateService(AppSettings settings)
    {
        return new OpenAITranslationService(settings);
    }
}
