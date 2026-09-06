using QuickTranslate.Models;

namespace QuickTranslate.Services;

/// <summary>Bridges Worker OCR blocks to the existing batch translation provider.</summary>
public sealed class MangaTranslationOrchestrator
{
    private readonly IScreenshotBatchTranslationService _translator;

    public MangaTranslationOrchestrator(IScreenshotBatchTranslationService translator) =>
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));

    public async Task<IReadOnlyList<MangaTranslationBlock>> TranslateAsync(
        IReadOnlyList<MangaTranslationBlock> blocks,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (string.IsNullOrWhiteSpace(targetLanguage))
            throw new ArgumentException("目标语言不能为空。", nameof(targetLanguage));

        var units = blocks.Select(block => new ScreenshotTranslationUnit(
            block.BlockId,
            block.SourceText,
            new[] { new OcrTextBlock(block.BlockId, block.SourceText, block.Bounds) },
            block.Bounds)).ToArray();
        var translated = await _translator.TranslateScreenshotBatchAsync(units, targetLanguage, cancellationToken).ConfigureAwait(false);
        var map = translated.ToDictionary(static x => x.UnitId, static x => x.Translation, StringComparer.Ordinal);
        return MangaTranslationResultMapper.Apply(blocks, map);
    }

    public static IReadOnlyList<ScreenshotTranslationUnit> CreateUnits(MangaWorkerResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!string.Equals(response.Type, "completed", StringComparison.Ordinal) ||
            !response.Payload.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Array.Empty<ScreenshotTranslationUnit>();
        var result = new List<ScreenshotTranslationUnit>();
        foreach (var item in blocks.EnumerateArray())
        {
            var id = item.GetProperty("block_id").GetString() ?? string.Empty;
            var text = item.TryGetProperty("source_text", out var source) && source.ValueKind == System.Text.Json.JsonValueKind.String
                ? source.GetString() ?? string.Empty : string.Empty;
            var bounds = item.GetProperty("bounds").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (bounds.Length != 4 || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text)) continue;
            var ocrBounds = new OcrBounds(bounds[0], bounds[1], bounds[2] - bounds[0], bounds[3] - bounds[1]);
            var block = new OcrTextBlock(id, text, ocrBounds);
            result.Add(new ScreenshotTranslationUnit(id, text, new[] { block }, ocrBounds));
        }
        return result;
    }
}
