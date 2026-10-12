using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed record MangaLayout(
    OcrBounds Bounds,
    double FontSize,
    string Alignment = "center",
    bool Vertical = false);

public sealed record MangaTranslationBlock(
    string BlockId,
    OcrBounds Bounds,
    string SourceText,
    string? Translation,
    string SourceLanguage,
    string OcrEngine,
    MangaLayout? Layout);

/// <summary>Safely applies provider translations to Worker blocks by stable block ID.</summary>
public static class MangaTranslationResultMapper
{
    public static IReadOnlyList<MangaTranslationBlock> Apply(
        IReadOnlyList<MangaTranslationBlock> blocks,
        IReadOnlyDictionary<string, string> translations)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(translations);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.BlockId) || !ids.Add(block.BlockId))
                throw new InvalidOperationException("漫画文本块 ID 无效或重复。");
            if (!translations.TryGetValue(block.BlockId, out var translation) || string.IsNullOrWhiteSpace(translation))
                throw new InvalidOperationException($"缺少漫画文本块译文：{block.BlockId}");
        }
        if (translations.Keys.Any(id => !ids.Contains(id)))
            throw new InvalidOperationException("译文包含未知漫画文本块 ID。");
        return blocks.Select(block => block with { Translation = translations[block.BlockId] }).ToArray();
    }

    public static MangaLayout CreateDefaultLayout(OcrBounds bounds, double? orientationDegrees = null) =>
        new(bounds, Math.Clamp(Math.Min(bounds.Width, bounds.Height) * 0.42, 10, 48), "center",
            orientationDegrees is >= 80 and <= 100 or <= -80 and >= -100);
}
