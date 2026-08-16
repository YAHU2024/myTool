namespace QuickTranslate.Models;

public enum LanguageRelation
{
    Different,
    Same,
    Unknown
}

public enum LanguageDetectionConfidence
{
    None,
    Low,
    High
}

public enum SourceLanguageFamily
{
    Unknown,
    Han,
    Japanese,
    Korean,
    Latin,
    Cyrillic,
    Arabic,
    Thai
}

public enum TranslationDirectionPreference
{
    Auto,
    RequestedTarget,
    FallbackTarget
}

public enum TranslationDirectionReason
{
    AutoDetectionDisabled,
    ModeDoesNotUseFallback,
    SourceMatchesRequestedTarget,
    SourceDiffersFromRequestedTarget,
    SourceLanguageUnknown,
    TargetLanguageUnsupported,
    UserSelectedTarget,
    UserSelectedFallback
}

public sealed record TranslationDirectionDecision(
    string RequestedTargetLanguage,
    string EffectiveTargetLanguage,
    LanguageRelation Relation,
    LanguageDetectionConfidence Confidence,
    SourceLanguageFamily SourceLanguageFamily,
    TranslationDirectionReason Reason)
{
    public bool FallbackUsed =>
        !string.Equals(RequestedTargetLanguage, EffectiveTargetLanguage, StringComparison.Ordinal);
}
