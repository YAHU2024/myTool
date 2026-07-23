using System.Text.Json;
using QuickTranslate.Helpers;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class AnalysisPromptProfileTests
{
    [Fact]
    public void Resolve_SelectedCustomProfile_ReplacesTargetLanguage()
    {
        var settings = new AppSettings
        {
            SelectedAnalysisPromptId = "custom:technical",
            AnalysisPromptProfiles =
            [
                new AnalysisPromptProfile
                {
                    Id = "custom:technical",
                    Name = "Technical",
                    Prompt = "Explain architecture in {targetLang}."
                },
                new AnalysisPromptProfile
                {
                    Id = "custom:other",
                    Name = "Other",
                    Prompt = "OTHER"
                }
            ]
        };

        var prompt = AnalysisPromptCatalog.Resolve(settings, "简体中文");

        Assert.Equal("Explain architecture in 简体中文.", prompt);
        Assert.DoesNotContain("OTHER", prompt);
    }

    [Theory]
    [InlineData("builtin:general", "grammar, structure, and relevant context")]
    [InlineData("builtin:learner", "pronunciation when relevant")]
    [InlineData("builtin:literary", "imagery, symbolism, context, and style")]
    [InlineData("builtin:business", "industry terms, implications, and action items")]
    public void Resolve_BuiltInProfile_UsesSelectedContract(string id, string expected)
    {
        var settings = new AppSettings { SelectedAnalysisPromptId = id };

        var prompt = AnalysisPromptCatalog.Resolve(settings, "English");

        Assert.Contains(expected, prompt);
        Assert.Contains("English", prompt);
    }

    [Fact]
    public void MigratePromptSettings_LegacyCustomPrompt_CreatesAndSelectsProfileOnce()
    {
        const string json = """
            {
              "CustomTranslationPrompt": "TRANSLATE",
              "CustomAnalysisPrompt": "LEGACY {targetLang}",
              "AnalysisPreset": "literary"
            }
            """;
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        using var document = JsonDocument.Parse(json);

        var changed = ConfigManager.MigratePromptSettings(settings, document.RootElement);

        Assert.True(changed);
        Assert.StartsWith("custom:", settings.SelectedAnalysisPromptId, StringComparison.Ordinal);
        var profile = Assert.Single(settings.AnalysisPromptProfiles);
        Assert.Equal(settings.SelectedAnalysisPromptId, profile.Id);
        Assert.Equal("原自定义解析", profile.Name);
        Assert.Equal("LEGACY {targetLang}", profile.Prompt);

        var migratedJson = JsonSerializer.Serialize(settings);
        using var migratedDocument = JsonDocument.Parse(migratedJson);
        Assert.False(ConfigManager.MigratePromptSettings(settings, migratedDocument.RootElement));
        Assert.Single(settings.AnalysisPromptProfiles);
    }

    [Fact]
    public void MigratePromptSettings_LegacySharedPrompt_CopiesItToTranslationAndAnalysisProfile()
    {
        const string json = """
            {
              "CustomSystemPrompt": "LEGACY {targetLang}"
            }
            """;
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        using var document = JsonDocument.Parse(json);

        ConfigManager.MigratePromptSettings(settings, document.RootElement);

        Assert.Equal("LEGACY {targetLang}", settings.CustomTranslationPrompt);
        Assert.Equal("LEGACY {targetLang}", settings.CustomAnalysisPrompt);
        Assert.Equal("LEGACY {targetLang}", Assert.Single(settings.AnalysisPromptProfiles).Prompt);
    }

    [Theory]
    [InlineData("general", "builtin:general")]
    [InlineData("learner", "builtin:learner")]
    [InlineData("literary", "builtin:literary")]
    [InlineData("business", "builtin:business")]
    public void MigratePromptSettings_LegacyPreset_SelectsBuiltIn(string preset, string expectedId)
    {
        var json = $$"""{"AnalysisPreset":"{{preset}}","CustomAnalysisPrompt":""}""";
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        using var document = JsonDocument.Parse(json);

        var changed = ConfigManager.MigratePromptSettings(settings, document.RootElement);

        Assert.True(changed);
        Assert.Equal(expectedId, settings.SelectedAnalysisPromptId);
        Assert.Empty(settings.AnalysisPromptProfiles);
    }

    [Fact]
    public void MigratePromptSettings_MissingSelectedCustomProfile_FallsBackToGeneral()
    {
        const string json = """
            {
              "SelectedAnalysisPromptId": "custom:missing",
              "AnalysisPromptProfiles": []
            }
            """;
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        using var document = JsonDocument.Parse(json);

        var changed = ConfigManager.MigratePromptSettings(settings, document.RootElement);

        Assert.True(changed);
        Assert.Equal(AnalysisPromptCatalog.GeneralId, settings.SelectedAnalysisPromptId);
    }
}
