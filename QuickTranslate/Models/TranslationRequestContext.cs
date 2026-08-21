namespace QuickTranslate.Models;

/// <summary>
/// Immutable settings captured when a floating-result session starts.
/// Later settings changes affect only newly created sessions.
/// </summary>
public sealed record TranslationRequestContext(
    string ApiBaseUrl,
    string ApiKey,
    string ModelName,
    bool? EnableThinking,
    string RequestedTargetLanguage,
    string FallbackLanguage,
    bool AutoDetectLanguage,
    string CustomTranslationPrompt,
    string SelectedAnalysisPromptId,
    IReadOnlyList<AnalysisPromptProfile> AnalysisPromptProfiles)
{
    internal static TranslationRequestContext CreateDefault(string targetLanguage = "English") =>
        new(
            "https://example.invalid/v1",
            string.Empty,
            "test-model",
            null,
            targetLanguage,
            targetLanguage == "English" ? "简体中文" : "English",
            false,
            string.Empty,
            "builtin:general",
            Array.Empty<AnalysisPromptProfile>());
}
