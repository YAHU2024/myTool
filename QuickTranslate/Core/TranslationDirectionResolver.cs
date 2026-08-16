using System.Text.RegularExpressions;
using QuickTranslate.Models;

namespace QuickTranslate.Core;

internal static partial class TranslationDirectionResolver
{
    private const int MinimumScriptCharacters = 8;
    private const double DominantScriptShare = 0.70;
    private const double MinimumJapaneseKanaShare = 0.10;

    public static TranslationDirectionDecision Resolve(
        string text,
        string requestedTargetLanguage,
        string fallbackLanguage,
        bool autoDetectLanguage,
        ContentType contentType,
        TranslationDirectionPreference preference = TranslationDirectionPreference.Auto)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedTargetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackLanguage);

        if (contentType != ContentType.Translation)
        {
            return CreateUnchanged(
                requestedTargetLanguage,
                TranslationDirectionReason.ModeDoesNotUseFallback);
        }

        if (preference == TranslationDirectionPreference.RequestedTarget)
        {
            return CreateManual(
                requestedTargetLanguage,
                requestedTargetLanguage,
                TranslationDirectionReason.UserSelectedTarget);
        }

        if (preference == TranslationDirectionPreference.FallbackTarget)
        {
            return CreateManual(
                requestedTargetLanguage,
                fallbackLanguage,
                TranslationDirectionReason.UserSelectedFallback);
        }

        if (!autoDetectLanguage)
        {
            return CreateUnchanged(
                requestedTargetLanguage,
                TranslationDirectionReason.AutoDetectionDisabled);
        }

        var targetFamily = GetTargetFamily(requestedTargetLanguage);
        if (targetFamily == SourceLanguageFamily.Unknown)
        {
            return new(
                requestedTargetLanguage,
                requestedTargetLanguage,
                LanguageRelation.Unknown,
                LanguageDetectionConfidence.None,
                SourceLanguageFamily.Unknown,
                TranslationDirectionReason.TargetLanguageUnsupported);
        }

        var sourceFamily = DetectDominantFamily(text);
        if (sourceFamily == SourceLanguageFamily.Unknown)
        {
            return new(
                requestedTargetLanguage,
                requestedTargetLanguage,
                LanguageRelation.Unknown,
                LanguageDetectionConfidence.Low,
                SourceLanguageFamily.Unknown,
                TranslationDirectionReason.SourceLanguageUnknown);
        }

        // Script evidence cannot safely distinguish English, French, German and
        // the other supported Latin-script languages. Treat same-script results
        // as unknown instead of switching to the fallback language.
        if (targetFamily == SourceLanguageFamily.Latin && sourceFamily == SourceLanguageFamily.Latin)
        {
            return new(
                requestedTargetLanguage,
                requestedTargetLanguage,
                LanguageRelation.Unknown,
                LanguageDetectionConfidence.Low,
                sourceFamily,
                TranslationDirectionReason.SourceLanguageUnknown);
        }

        if (sourceFamily == targetFamily)
        {
            return new(
                requestedTargetLanguage,
                fallbackLanguage,
                LanguageRelation.Same,
                LanguageDetectionConfidence.High,
                sourceFamily,
                TranslationDirectionReason.SourceMatchesRequestedTarget);
        }

        return new(
            requestedTargetLanguage,
            requestedTargetLanguage,
            LanguageRelation.Different,
            LanguageDetectionConfidence.High,
            sourceFamily,
            TranslationDirectionReason.SourceDiffersFromRequestedTarget);
    }

    internal static SourceLanguageFamily DetectDominantFamily(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return SourceLanguageFamily.Unknown;

        var prose = TechnicalSegmentRegex().Replace(text, " ");
        var counts = new Dictionary<SourceLanguageFamily, int>
        {
            [SourceLanguageFamily.Han] = 0,
            [SourceLanguageFamily.Korean] = 0,
            [SourceLanguageFamily.Latin] = 0,
            [SourceLanguageFamily.Cyrillic] = 0,
            [SourceLanguageFamily.Arabic] = 0,
            [SourceLanguageFamily.Thai] = 0
        };
        var kanaCount = 0;

        foreach (var rune in prose.EnumerateRunes())
        {
            var value = rune.Value;
            if (IsHan(value))
                counts[SourceLanguageFamily.Han]++;
            else if (IsKana(value))
                kanaCount++;
            else if (IsHangul(value))
                counts[SourceLanguageFamily.Korean]++;
            else if (IsLatin(value))
                counts[SourceLanguageFamily.Latin]++;
            else if (IsCyrillic(value))
                counts[SourceLanguageFamily.Cyrillic]++;
            else if (IsArabic(value))
                counts[SourceLanguageFamily.Arabic]++;
            else if (IsThai(value))
                counts[SourceLanguageFamily.Thai]++;
        }

        var total = counts.Values.Sum() + kanaCount;
        if (total < MinimumScriptCharacters)
            return SourceLanguageFamily.Unknown;

        var hanCount = counts[SourceLanguageFamily.Han];
        var japaneseCount = hanCount + kanaCount;
        if (kanaCount >= 2 &&
            japaneseCount >= MinimumScriptCharacters &&
            (double)japaneseCount / total >= DominantScriptShare &&
            (double)kanaCount / japaneseCount >= MinimumJapaneseKanaShare)
        {
            return SourceLanguageFamily.Japanese;
        }

        var dominant = counts.MaxBy(pair => pair.Value);
        return dominant.Value >= MinimumScriptCharacters &&
               (double)dominant.Value / total >= DominantScriptShare
            ? dominant.Key
            : SourceLanguageFamily.Unknown;
    }

    private static TranslationDirectionDecision CreateUnchanged(
        string requestedTargetLanguage,
        TranslationDirectionReason reason) =>
        new(
            requestedTargetLanguage,
            requestedTargetLanguage,
            LanguageRelation.Unknown,
            LanguageDetectionConfidence.None,
            SourceLanguageFamily.Unknown,
            reason);

    private static TranslationDirectionDecision CreateManual(
        string requestedTargetLanguage,
        string effectiveTargetLanguage,
        TranslationDirectionReason reason) =>
        new(
            requestedTargetLanguage,
            effectiveTargetLanguage,
            LanguageRelation.Unknown,
            LanguageDetectionConfidence.None,
            SourceLanguageFamily.Unknown,
            reason);

    private static SourceLanguageFamily GetTargetFamily(string targetLanguage) =>
        targetLanguage switch
        {
            "简体中文" or "繁体中文" => SourceLanguageFamily.Han,
            "日本語" => SourceLanguageFamily.Japanese,
            "한국어" => SourceLanguageFamily.Korean,
            "Русский" => SourceLanguageFamily.Cyrillic,
            "العربية" => SourceLanguageFamily.Arabic,
            "ไทย" => SourceLanguageFamily.Thai,
            "English" or "Français" or "Deutsch" or "Español" or
                "Português" or "Italiano" or "Tiếng Việt" => SourceLanguageFamily.Latin,
            _ => SourceLanguageFamily.Unknown
        };

    private static bool IsHan(int value) =>
        value is >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF or
            >= 0x20000 and <= 0x2EBEF;

    private static bool IsKana(int value) => value is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF;

    private static bool IsHangul(int value) => value is >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xAC00 and <= 0xD7AF;

    private static bool IsLatin(int value) => value is >= 0x0041 and <= 0x005A or >= 0x0061 and <= 0x007A or >= 0x00C0 and <= 0x024F or >= 0x1E00 and <= 0x1EFF;

    private static bool IsCyrillic(int value) => value is >= 0x0400 and <= 0x052F;

    private static bool IsArabic(int value) => value is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF;

    private static bool IsThai(int value) => value is >= 0x0E00 and <= 0x0E7F;

    [GeneratedRegex(@"(?is)```.*?```|~~~.*?~~~|`[^`\r\n]*`|https?://\S+|\]\([^)]+\)|(?:[A-Z]:\\|(?:^|\s)(?:\.?\.?[/\\]))\S+")]
    private static partial Regex TechnicalSegmentRegex();
}
