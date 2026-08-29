using System.Text.Json;
using QuickTranslate.Core;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed record ScreenshotTranslationUnit(
    string UnitId,
    string SourceText,
    IReadOnlyList<OcrTextBlock> Blocks,
    OcrBounds Bounds);

public sealed record TranslatedTextUnit(
    string UnitId,
    string Translation);

public sealed record ScreenshotTranslationMappingResult(
    bool Accepted,
    string Reason,
    IReadOnlyList<TranslatedTextUnit> MappedUnits,
    IReadOnlyList<ScreenshotTranslationUnit> UnmappedUnits)
{
    public int MappedCount => MappedUnits.Count;
}

/// <summary>
/// 只按稳定 UnitId 映射译文，拒绝任何无法安全定位的响应。
/// </summary>
public static class ScreenshotTranslationMapper
{
    public static IReadOnlyList<ScreenshotTranslationUnit> CreateUnits(
        IReadOnlyList<OcrParagraph> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        return paragraphs
            .Select((paragraph, index) => new ScreenshotTranslationUnit(
                $"u{index + 1:0000}",
                paragraph.SourceText,
                paragraph.Lines,
                paragraph.Bounds))
            .ToArray();
    }

    public static ScreenshotTranslationMappingResult Map(
        IReadOnlyList<ScreenshotTranslationUnit> expected,
        IReadOnlyList<TranslatedTextUnit> translated)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(translated);

        var expectedIds = expected.Select(static unit => unit.UnitId).ToArray();
        if (expected.Any(static unit => string.IsNullOrWhiteSpace(unit.UnitId)) ||
            expectedIds.Distinct(StringComparer.Ordinal).Count() != expectedIds.Length)
        {
            return Reject("invalid_expected_id", expected);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var mapped = new List<TranslatedTextUnit>(translated.Count);
        foreach (var unit in translated)
        {
            if (string.IsNullOrWhiteSpace(unit.UnitId))
                return Reject("invalid_id", expected, mapped);
            if (!seen.Add(unit.UnitId))
                return Reject("duplicate_id", expected, mapped);
            if (!expectedIds.Contains(unit.UnitId, StringComparer.Ordinal))
                return Reject("unexpected_id", expected, mapped);
            if (string.IsNullOrWhiteSpace(unit.Translation))
                return Reject("empty_translation", expected, mapped);
            mapped.Add(unit);
        }

        if (seen.Count != expectedIds.Length || expectedIds.Any(id => !seen.Contains(id)))
            return Reject("missing_id", expected, mapped);

        var ordered = expectedIds
            .Select(id => mapped.Single(unit => string.Equals(unit.UnitId, id, StringComparison.Ordinal)))
            .ToArray();
        return new(true, "ok", ordered, Array.Empty<ScreenshotTranslationUnit>());
    }

    public static ScreenshotTranslationMappingResult ParseAndMap(
        string json,
        IReadOnlyList<ScreenshotTranslationUnit> expected)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(expected);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("units", out var units) ||
                units.ValueKind != JsonValueKind.Array)
            {
                return Reject("missing_units", expected);
            }

            var translated = new List<TranslatedTextUnit>();
            foreach (var element in units.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    !element.TryGetProperty("translation", out var translation) ||
                    translation.ValueKind != JsonValueKind.String)
                {
                    return Reject("invalid_unit", expected, translated);
                }

                translated.Add(new(
                    id.GetString() ?? string.Empty,
                    translation.GetString() ?? string.Empty));
            }

            return Map(expected, translated);
        }
        catch (JsonException)
        {
            return Reject("invalid_json", expected);
        }
    }

    private static ScreenshotTranslationMappingResult Reject(
        string reason,
        IReadOnlyList<ScreenshotTranslationUnit> expected,
        IReadOnlyList<TranslatedTextUnit>? mapped = null) =>
        new(false, reason, mapped ?? Array.Empty<TranslatedTextUnit>(), expected.ToArray());
}
