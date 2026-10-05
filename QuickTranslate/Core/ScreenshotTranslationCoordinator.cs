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
        CancellationToken cancellationToken = default,
        Action<IReadOnlyList<ScreenshotTranslationUnit>>? onUnitsReady = null,
        Action<TranslatedTextUnit>? onUnitTranslated = null,
        Func<ScreenshotTranslationUnit, bool>? shouldTranslate = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(translateAsync);
        image.Validate(_limits);

        var ocrWatch = Stopwatch.StartNew();
        var ocrResult = await _ocrService
            .RecognizeAsync(image, options, cancellationToken)
            .ConfigureAwait(false);
        ocrWatch.Stop();

        return await ExecuteWithOcrResultAsync(
            image,
            ocrResult,
            translateAsync,
            ocrWatch.Elapsed,
            cancellationToken,
            onUnitsReady,
            onUnitTranslated,
            shouldTranslate).ConfigureAwait(false);
    }

    public async Task<ScreenshotTranslationPipelineResult> ExecuteWithOcrResultAsync(
        OcrImage image,
        OcrResult ocrResult,
        Func<IReadOnlyList<ScreenshotTranslationUnit>, CancellationToken, Task<IReadOnlyList<TranslatedTextUnit>>> translateAsync,
        TimeSpan ocrElapsed,
        CancellationToken cancellationToken = default,
        Action<IReadOnlyList<ScreenshotTranslationUnit>>? onUnitsReady = null,
        Action<TranslatedTextUnit>? onUnitTranslated = null,
        Func<ScreenshotTranslationUnit, bool>? shouldTranslate = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ocrResult);
        ArgumentNullException.ThrowIfNull(translateAsync);
        image.Validate(_limits);

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

        var units = ScreenshotTranslationMapper.CreateUnits(paragraphs)
            .Where(unit => shouldTranslate?.Invoke(unit) ?? true)
            .ToArray();
        if (units.Length > _limits.MaxTranslationUnitCount)
            throw new ArgumentException("翻译单元数超过允许上限。", nameof(ocrResult));

        if (units.Length == 0)
        {
            var emptyMapping = ScreenshotTranslationMapper.Map(units, Array.Empty<TranslatedTextUnit>());
            return new(ScreenshotTranslationPipelineStatus.NoText, ocrResult, units, emptyMapping)
            {
                Timings = new(
                    ocrElapsed,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    normalizedBlocks.Length,
                    0)
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        onUnitsReady?.Invoke(units);

        var translationWatch = Stopwatch.StartNew();
        var translated = await translateAsync(units, cancellationToken).ConfigureAwait(false);
        translationWatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        if (onUnitTranslated is not null)
        {
            foreach (var unit in translated)
                onUnitTranslated(unit);
        }
        var mappingWatch = Stopwatch.StartNew();
        var mapping = ScreenshotTranslationMapper.Map(units, translated);
        mappingWatch.Stop();
        var status = mapping.Accepted
            ? ScreenshotTranslationPipelineStatus.Completed
            : ScreenshotTranslationPipelineStatus.TranslationMappingRejected;
        return new(status, ocrResult, units, mapping)
        {
            Timings = new(
                ocrElapsed,
                translationWatch.Elapsed,
                mappingWatch.Elapsed,
                normalizedBlocks.Length,
                    units.Length)
        };
    }

    public async Task<ScreenshotTranslationPipelineResult> ExecuteWithUnitsAsync(
        OcrResult ocrResult,
        IReadOnlyList<ScreenshotTranslationUnit> units,
        Func<IReadOnlyList<ScreenshotTranslationUnit>, CancellationToken, Task<IReadOnlyList<TranslatedTextUnit>>> translateAsync,
        TimeSpan ocrElapsed,
        CancellationToken cancellationToken = default,
        Action<IReadOnlyList<ScreenshotTranslationUnit>>? onUnitsReady = null,
        Action<TranslatedTextUnit>? onUnitTranslated = null,
        Func<ScreenshotTranslationUnit, bool>? shouldTranslate = null)
    {
        ArgumentNullException.ThrowIfNull(ocrResult);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(translateAsync);
        units = units
            .Where(unit => shouldTranslate?.Invoke(unit) ?? true)
            .ToArray();

        if (units.Count == 0)
        {
            var empty = ScreenshotTranslationMapper.Map(units, Array.Empty<TranslatedTextUnit>());
            return new(ScreenshotTranslationPipelineStatus.NoText, ocrResult, units, empty)
            {
                Timings = new(ocrElapsed, TimeSpan.Zero, TimeSpan.Zero, ocrResult.Blocks.Count, 0)
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        onUnitsReady?.Invoke(units);
        var translationWatch = Stopwatch.StartNew();
        var translated = await translateAsync(units, cancellationToken).ConfigureAwait(false);
        translationWatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var unit in translated)
            onUnitTranslated?.Invoke(unit);
        var mappingWatch = Stopwatch.StartNew();
        var mapping = ScreenshotTranslationMapper.Map(units, translated);
        mappingWatch.Stop();
        var status = mapping.Accepted
            ? ScreenshotTranslationPipelineStatus.Completed
            : ScreenshotTranslationPipelineStatus.TranslationMappingRejected;
        return new(status, ocrResult, units, mapping)
        {
            Timings = new(ocrElapsed, translationWatch.Elapsed, mappingWatch.Elapsed, ocrResult.Blocks.Count, units.Count)
        };
    }

    public async Task<IReadOnlyList<TranslatedTextUnit>> TranslateUnitsAsync(
        IReadOnlyList<ScreenshotTranslationUnit> units,
        Func<IReadOnlyList<ScreenshotTranslationUnit>, CancellationToken, Task<IReadOnlyList<TranslatedTextUnit>>> translateAsync,
        CancellationToken cancellationToken = default,
        Action<TranslatedTextUnit>? onUnitTranslated = null)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(translateAsync);
        if (units.Count == 0) return Array.Empty<TranslatedTextUnit>();
        cancellationToken.ThrowIfCancellationRequested();
        var translated = await translateAsync(units, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var unit in translated) onUnitTranslated?.Invoke(unit);
        var mapping = ScreenshotTranslationMapper.Map(units, translated);
        if (!mapping.Accepted) throw new ScreenshotTranslationBatchFormatException(mapping.Reason);
        return mapping.MappedUnits;
    }
}
