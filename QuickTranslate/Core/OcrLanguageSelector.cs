namespace QuickTranslate.Core;

public sealed record OcrLanguageSelection(
    string? RequestedLanguageTag,
    string? SelectedLanguageTag,
    bool FallbackUsed,
    bool IsAvailable,
    string Reason);

/// <summary>只依赖语言标签列表的 OCR 语言选择逻辑。</summary>
public static class OcrLanguageSelector
{
    public static OcrLanguageSelection Select(
        IReadOnlyList<string> availableLanguageTags,
        string? languageHint,
        bool allowFallback,
        string? userProfileLanguageTag = null)
    {
        ArgumentNullException.ThrowIfNull(availableLanguageTags);
        var available = availableLanguageTags
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (available.Length == 0)
            return new(languageHint, null, false, false, "no_available_language");

        var exact = FindBestMatch(available, languageHint);
        if (exact is not null)
            return new(languageHint, exact, false, true, "requested_language");

        if (!string.IsNullOrWhiteSpace(languageHint) && !allowFallback)
            return new(languageHint, null, false, false, "requested_language_unavailable");

        var profile = FindBestMatch(available, userProfileLanguageTag);
        var selected = profile ?? available[0];
        return new(languageHint, selected, true, true, "fallback_language");
    }

    private static string? FindBestMatch(IReadOnlyList<string> available, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null;

        return available.FirstOrDefault(tag => AreCompatibleLanguageTags(tag, requested));
    }

    private static bool AreCompatibleLanguageTags(string first, string second)
    {
        var firstParts = first.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var secondParts = second.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var commonLength = Math.Min(firstParts.Length, secondParts.Length);
        for (var index = 0; index < commonLength; index++)
        {
            if (!string.Equals(firstParts[index], secondParts[index], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return commonLength == firstParts.Length || commonLength == secondParts.Length;
    }
}
