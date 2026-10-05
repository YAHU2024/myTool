using System.Text;
using System.Text.RegularExpressions;
using QuickTranslate.Models;

namespace QuickTranslate.Core;

/// <summary>
/// Decides whether one OCR unit should be sent to the translation provider.
/// The decision is deliberately local and deterministic so callers can filter
/// screenshot units before opening a provider request.
/// </summary>
public enum ScreenshotTranslationAction
{
    Translate,
    Preserve,
    Skip
}

public enum ScreenshotTranslationEligibilityReason
{
    NaturalLanguage,
    EmptyText,
    NonText,
    Url,
    FilePath,
    Code,
    Identifier,
    TargetLanguageMatch,
    SourceLanguageUnknown
}

public sealed record ScreenshotTranslationEligibility(
    ScreenshotTranslationAction Action,
    ScreenshotTranslationEligibilityReason Reason,
    SourceLanguageFamily SourceLanguageFamily = SourceLanguageFamily.Unknown,
    ContentType? DetectedContentType = null)
{
    public bool ShouldTranslate => Action == ScreenshotTranslationAction.Translate;

    public bool ShouldPreserve => Action == ScreenshotTranslationAction.Preserve;

    public bool ShouldSkip => Action == ScreenshotTranslationAction.Skip;
}

/// <summary>
/// Screenshot-specific translation qualification. This does not alter OCR
/// text or layout units; it only classifies each unit for the next pipeline
/// stage.
/// </summary>
public static partial class ScreenshotTranslationEligibilityEvaluator
{
    private static readonly HashSet<string> CommonEnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "be", "by", "can", "for", "from", "has", "have",
        "hello", "how", "in", "is", "it", "of", "on", "or", "please", "that", "the", "this",
        "to", "was", "were", "what", "when", "where", "which", "with", "will", "world", "you"
    };

    public static ScreenshotTranslationEligibility Evaluate(
        string text,
        string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return new(
                ScreenshotTranslationAction.Skip,
                ScreenshotTranslationEligibilityReason.EmptyText);
        }

        if (!ContainsLetter(trimmed))
        {
            return new(
                ScreenshotTranslationAction.Skip,
                ScreenshotTranslationEligibilityReason.NonText);
        }

        if (UrlRegex().IsMatch(trimmed))
        {
            return new(
                ScreenshotTranslationAction.Preserve,
                ScreenshotTranslationEligibilityReason.Url);
        }

        if (FilePathRegex().IsMatch(trimmed))
        {
            return new(
                ScreenshotTranslationAction.Preserve,
                ScreenshotTranslationEligibilityReason.FilePath);
        }

        var detection = ContentTypeDetector.DetectDetailed(trimmed);
        if (detection.ContentType == ContentType.Code)
        {
            return new(
                ScreenshotTranslationAction.Preserve,
                ScreenshotTranslationEligibilityReason.Code,
                DetectedContentType: ContentType.Code);
        }

        if (LooksLikeIdentifier(trimmed))
        {
            return new(
                ScreenshotTranslationAction.Preserve,
                ScreenshotTranslationEligibilityReason.Identifier,
                DetectedContentType: detection.ContentType);
        }

        var sourceFamily = DetectSourceLanguage(trimmed);
        var targetFamily = GetTargetLanguageFamily(targetLanguage);
        if (sourceFamily != SourceLanguageFamily.Unknown &&
            sourceFamily == targetFamily)
        {
            return new(
                ScreenshotTranslationAction.Preserve,
                ScreenshotTranslationEligibilityReason.TargetLanguageMatch,
                sourceFamily,
                detection.ContentType);
        }

        return new(
            ScreenshotTranslationAction.Translate,
            sourceFamily == SourceLanguageFamily.Unknown
                ? ScreenshotTranslationEligibilityReason.SourceLanguageUnknown
                : ScreenshotTranslationEligibilityReason.NaturalLanguage,
            sourceFamily,
            detection.ContentType);
    }

    private static bool ContainsLetter(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.UppercaseLetter or
                System.Globalization.UnicodeCategory.LowercaseLetter or
                System.Globalization.UnicodeCategory.TitlecaseLetter or
                System.Globalization.UnicodeCategory.ModifierLetter or
                System.Globalization.UnicodeCategory.OtherLetter)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeIdentifier(string text)
    {
        if (text.Any(char.IsWhiteSpace) || !IdentifierCharactersRegex().IsMatch(text))
            return false;

        if (text.Contains('_') || text.Contains('$') || text.Contains("::", StringComparison.Ordinal) ||
            text.Contains("->", StringComparison.Ordinal) || text.Contains('#'))
        {
            return true;
        }

        var hasLetter = false;
        var hasDigit = false;
        foreach (var c in text)
        {
            hasLetter |= char.IsLetter(c);
            hasDigit |= char.IsDigit(c);
        }

        if (hasLetter && hasDigit)
            return true;

        return CamelCaseIdentifierRegex().IsMatch(text);
    }

    private static SourceLanguageFamily DetectSourceLanguage(string text)
    {
        var han = 0;
        var kana = 0;
        var hangul = 0;
        var cyrillic = 0;
        var arabic = 0;
        var thai = 0;
        var latin = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (IsHan(value)) han++;
            else if (IsKana(value)) kana++;
            else if (IsHangul(value)) hangul++;
            else if (IsCyrillic(value)) cyrillic++;
            else if (IsArabic(value)) arabic++;
            else if (IsThai(value)) thai++;
            else if (IsLatin(value)) latin++;
        }

        var total = han + kana + hangul + cyrillic + arabic + thai + latin;
        if (total == 0)
            return SourceLanguageFamily.Unknown;

        var japanese = han + kana;
        if (kana >= 2 && japanese >= 4 && Share(japanese, total) >= 0.55)
            return SourceLanguageFamily.Japanese;
        if (han >= 2 && Share(han, total) >= 0.55)
            return SourceLanguageFamily.Han;
        if (hangul >= 3 && Share(hangul, total) >= 0.55)
            return SourceLanguageFamily.Korean;
        if (cyrillic >= 3 && Share(cyrillic, total) >= 0.55)
            return SourceLanguageFamily.Cyrillic;
        if (arabic >= 3 && Share(arabic, total) >= 0.55)
            return SourceLanguageFamily.Arabic;
        if (thai >= 3 && Share(thai, total) >= 0.55)
            return SourceLanguageFamily.Thai;
        if (latin >= 4 && Share(latin, total) >= 0.65 && LooksLikeEnglish(text))
            return SourceLanguageFamily.Latin;

        return SourceLanguageFamily.Unknown;
    }

    private static bool LooksLikeEnglish(string text)
    {
        var words = EnglishWordRegex().Matches(text);
        if (words.Count == 0)
            return false;

        var commonWords = words
            .Cast<Match>()
            .Count(match => CommonEnglishWords.Contains(match.Value));
        return commonWords > 0 || (words.Count == 1 && words[0].Value.Length >= 4);
    }

    private static SourceLanguageFamily GetTargetLanguageFamily(string targetLanguage) =>
        targetLanguage.Trim() switch
        {
            "简体中文" or "繁体中文" or "简体中文（中国大陆）" or "繁體中文" => SourceLanguageFamily.Han,
            "日本語" or "日语" => SourceLanguageFamily.Japanese,
            "한국어" or "韩语" => SourceLanguageFamily.Korean,
            "Русский" or "俄语" => SourceLanguageFamily.Cyrillic,
            "العربية" or "阿拉伯语" => SourceLanguageFamily.Arabic,
            "ไทย" or "泰语" => SourceLanguageFamily.Thai,
            "English" or "英文" => SourceLanguageFamily.Latin,
            "Français" or "Deutsch" or "Español" or "Português" or "Italiano" or "Tiếng Việt" =>
                SourceLanguageFamily.Latin,
            _ => SourceLanguageFamily.Unknown
        };

    private static double Share(int count, int total) => (double)count / total;

    private static bool IsHan(int value) => value is >= 0x3400 and <= 0x4DBF or
        >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2EBEF;

    private static bool IsKana(int value) => value is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF;

    private static bool IsHangul(int value) => value is >= 0x1100 and <= 0x11FF or
        >= 0x3130 and <= 0x318F or >= 0xAC00 and <= 0xD7AF;

    private static bool IsLatin(int value) => value is >= 0x0041 and <= 0x005A or
        >= 0x0061 and <= 0x007A or >= 0x00C0 and <= 0x024F or >= 0x1E00 and <= 0x1EFF;

    private static bool IsCyrillic(int value) => value is >= 0x0400 and <= 0x052F;

    private static bool IsArabic(int value) => value is >= 0x0600 and <= 0x06FF or
        >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF;

    private static bool IsThai(int value) => value is >= 0x0E00 and <= 0x0E7F;

    [GeneratedRegex(@"^(?:(?:https?|ftp)://|www\.)\S+$|^[\p{L}\p{N}_-]+\.(?:com|org|net|edu|gov|io|dev|cn|jp|co\.[a-z]{2})(?:[/:?#]\S*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^(?:[A-Za-z]:[\\/]|\\\\|\.{1,2}[\\/]|/).+$|^[^\s/\\]+\.[A-Za-z0-9]{1,8}(?:[\\/].*)?$", RegexOptions.Compiled)]
    private static partial Regex FilePathRegex();

    [GeneratedRegex(@"^[\p{L}\p{N}_$.:#<>\[\]{}()+*/=!?-]+$", RegexOptions.Compiled)]
    private static partial Regex IdentifierCharactersRegex();

    [GeneratedRegex(@"^[a-z]+[A-Z][A-Za-z0-9]*$", RegexOptions.Compiled)]
    private static partial Regex CamelCaseIdentifierRegex();

    [GeneratedRegex(@"[A-Za-z]+", RegexOptions.Compiled)]
    private static partial Regex EnglishWordRegex();
}
