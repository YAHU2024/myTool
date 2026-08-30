using System.Diagnostics;
using QuickTranslate.Models;
using QuickTranslate.Services;

namespace QuickTranslate.Core;

public enum ScreenshotTranslationPipelineStatus
{
    NoText,
    Completed,
    TranslationMappingRejected
}

public sealed record ScreenshotTranslationPipelineResult(
    ScreenshotTranslationPipelineStatus Status,
    OcrResult OcrResult,
    IReadOnlyList<ScreenshotTranslationUnit> Units,
    ScreenshotTranslationMappingResult Mapping)
{
    public ScreenshotTranslationStageTimings Timings { get; init; } = ScreenshotTranslationStageTimings.Empty;
}

/// <summary>
/// M1 的最小协调器：用 OCR 服务产出稳定单元，再交给可替换的批量翻译函数。
/// 它不负责截图、WPF 覆盖或真实 Provider 请求。
/// </summary>
public sealed class ScreenshotTranslationCoordinator
{
    private readonly IOcrService _ocrService;
    private readonly OcrResourceLimits _limits;

    public ScreenshotTranslationCoordinator(
        IOcrService ocrService,
        OcrResourceLimits? limits = null)
    {
        _ocrService = ocrService ?? throw new ArgumentNullException(nameof(ocrService));
        _limits = limits ?? OcrResourceLimits.Default;
    }

    public async Task<ScreenshotTranslationPipelineResult> ExecuteAsync(
        OcrImage image,
        Func<IReadOnlyList<ScreenshotTranslationUnit>, CancellationToken, Task<IReadOnlyList<TranslatedTextUnit>>> translateAsync,
        OcrRecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(translateAsync);
        image.Validate(_limits);

        var ocrWatch = Stopwatch.StartNew();
        var ocrResult = await _ocrService
            .RecognizeAsync(image, options, cancellationToken)
            .ConfigureAwait(false);
        ocrWatch.Stop();

        if (ocrResult.Blocks.Count > _limits.MaxBlockCount)
            throw new ArgumentException("OCR 块数超过允许上限。", nameof(ocrResult));

        var normalizedBlocks = ocrResult.Blocks
            .Select(static block => block with { Text = OcrTextNormalizer.Normalize(block.Text) })
            .Where(static block => block.Text.Length > 0)
            .ToArray();
        OcrBlockValidator.ValidateAll(normalizedBlocks, image.PixelWidth, image.PixelHeight);

        var paragraphs = OcrBlockAggregator.Aggregate(normalizedBlocks);
        if (paragraphs.Any(paragraph => paragraph.SourceText.Length > _limits.MaxNormalizedTextLength) ||
            paragraphs.Sum(static paragraph => (long)paragraph.SourceText.Length) > _limits.MaxNormalizedTextLength)
        {
            throw new ArgumentException("OCR 规范化文本超过允许上限。", nameof(ocrResult));
        }

        var units = ScreenshotTranslationMapper.CreateUnits(paragraphs);
        if (units.Count > _limits.MaxTranslationUnitCount)
            throw new ArgumentException("翻译单元数超过允许上限。", nameof(ocrResult));

        if (units.Count == 0)
        {
            var emptyMapping = ScreenshotTranslationMapper.Map(units, Array.Empty<TranslatedTextUnit>());
            return new(ScreenshotTranslationPipelineStatus.NoText, ocrResult, units, emptyMapping)
            {
                Timings = new(
                    ocrWatch.Elapsed,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    normalizedBlocks.Length,
                    0)
            };
        }

        var translationWatch = Stopwatch.StartNew();
        var translated = await translateAsync(units, cancellationToken).ConfigureAwait(false);
        translationWatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        var mappingWatch = Stopwatch.StartNew();
        var mapping = ScreenshotTranslationMapper.Map(units, translated);
        mappingWatch.Stop();
        var status = mapping.Accepted
            ? ScreenshotTranslationPipelineStatus.Completed
            : ScreenshotTranslationPipelineStatus.TranslationMappingRejected;
        return new(status, ocrResult, units, mapping)
        {
            Timings = new(
                ocrWatch.Elapsed,
                translationWatch.Elapsed,
                mappingWatch.Elapsed,
                normalizedBlocks.Length,
                units.Count)
        };
    }
}
