using QuickTranslate.Core;

namespace QuickTranslate.Models;

public enum TranslationRequestKind
{
    Translation,
    Analysis
}

/// <summary>
/// Immutable description of one API request. Settings changes only affect requests created later.
/// </summary>
public sealed record TranslationRequest(
    TranslationRequestKind Kind,
    string Text,
    string TargetLanguage,
    ContentType ContentType,
    string ApiBaseUrl,
    string ApiKey,
    string ModelName,
    string SystemPrompt,
    bool FallbackUsed);
