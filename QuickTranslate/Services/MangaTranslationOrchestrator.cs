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
}
