using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QuickTranslate.Services;

internal enum TranslationEchoConfidence
{
    None,
    Suspected,
    Confirmed
}

internal sealed record TranslationEchoDetectionResult(
    TranslationEchoConfidence Confidence,
    double Similarity,
    double LengthRatio,
    string Reason)
{
    public bool IsConfirmed => Confidence == TranslationEchoConfidence.Confirmed;
}

/// <summary>
/// Conservatively detects provider responses that repeat the translation input.
/// Only high-confidence results may retract the completed result and block persistence.
/// </summary>
internal static partial class TranslationEchoDetector
{
    internal const int MinimumSourceLength = 40;
    internal const int MinimumProseLength = 60;
    internal const double ConfirmedSimilarityThreshold = 0.97;
    internal const double SuspectedSimilarityThreshold = 0.90;

    public static TranslationEchoDetectionResult Detect(string? source, string? result)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(result))
            return None("empty");

        var normalizedSource = Normalize(source);
        var normalizedResult = Normalize(result);
        if (normalizedSource.Length < MinimumSourceLength || normalizedResult.Length == 0)
            return None("short_source");

        var lengthRatio = (double)normalizedResult.Length / normalizedSource.Length;
        var sourceProse = Normalize(RemoveTechnicalSegments(source));
        var resultProse = Normalize(RemoveTechnicalSegments(result));
        var enoughProse = HasEnoughProse(sourceProse);

        if (string.Equals(normalizedSource, normalizedResult, StringComparison.Ordinal))
        {
            return enoughProse
                ? new(TranslationEchoConfidence.Confirmed, 1, lengthRatio, "normalized_equal")
                : new(TranslationEchoConfidence.Suspected, 1, lengthRatio, "technical_equal");
        }

        if (lengthRatio is < 0.95 or > 1.05 || sourceProse.Length == 0 || resultProse.Length == 0)
            return None("length_changed");

        var similarity = BigramDice(sourceProse, resultProse);
        if (enoughProse && similarity >= ConfirmedSimilarityThreshold)
        {
            return new(
                TranslationEchoConfidence.Confirmed,
                similarity,
                lengthRatio,
                "prose_near_equal");
        }

        if (similarity >= SuspectedSimilarityThreshold)
        {
            return new(
                TranslationEchoConfidence.Suspected,
                similarity,
                lengthRatio,
                enoughProse ? "prose_similar" : "technical_similar");
        }

        return new(TranslationEchoConfidence.None, similarity, lengthRatio, "different");
    }

    private static bool HasEnoughProse(string text)
    {
        var cjkCount = text.Count(IsCjk);
        if (cjkCount >= MinimumSourceLength)
            return true;

        if (text.Length < MinimumProseLength)
            return false;

        return WordRegex().Matches(text).Count >= 8;
    }

    private static string RemoveTechnicalSegments(string text) =>
        TechnicalSegmentRegex().Replace(text, " ");

    private static string Normalize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
        }

        return builder.ToString().Trim();
    }

    private static double BigramDice(string left, string right)
    {
        if (left.Length < 2 || right.Length < 2)
            return string.Equals(left, right, StringComparison.Ordinal) ? 1 : 0;

        var leftBigrams = CountBigrams(left);
        var rightBigrams = CountBigrams(right);
        var intersection = 0;
        foreach (var pair in leftBigrams)
        {
            if (rightBigrams.TryGetValue(pair.Key, out var rightCount))
                intersection += Math.Min(pair.Value, rightCount);
        }

        var leftTotal = leftBigrams.Values.Sum();
        var rightTotal = rightBigrams.Values.Sum();
        return leftTotal + rightTotal == 0
            ? 0
            : 2.0 * intersection / (leftTotal + rightTotal);
    }

    private static Dictionary<string, int> CountBigrams(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < text.Length - 1; index++)
        {
            var bigram = text.Substring(index, 2);
            counts[bigram] = counts.TryGetValue(bigram, out var count) ? count + 1 : 1;
        }

        return counts;
    }

    private static bool IsCjk(char ch) => ch is >= '\u4E00' and <= '\u9FFF';

    private static TranslationEchoDetectionResult None(string reason) =>
        new(TranslationEchoConfidence.None, 0, 0, reason);

    [GeneratedRegex(@"(?ix)(?:https?://\S+|[A-Z]:\\\S+|(?:^|\s)(?:\.?\.?[/\\]|/)[^\s]+|\b\S+[/\\]\S+\b|\b[\w.-]+\.(?:cs|xaml|json|xml|md|txt|dll|exe|js|ts|tsx|jsx|py|java|kt|rs|go|sql|yaml|yml|toml)\b)")]
    private static partial Regex TechnicalSegmentRegex();

    [GeneratedRegex(@"\p{L}+(?:['’-]\p{L}+)?", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
