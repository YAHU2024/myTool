namespace QuickTranslate.Core;

/// <summary>
/// Privacy-safe timings and counts for one screenshot translation pipeline.
/// The record intentionally contains no image, OCR text, translation, or path.
/// </summary>
public sealed record ScreenshotTranslationStageTimings(
    TimeSpan OcrElapsed,
    TimeSpan TranslationElapsed,
    TimeSpan MappingElapsed,
    int OcrBlockCount,
    int TranslationUnitCount)
{
    public int RawBlockCount { get; init; }

    public int? NormalizedBlockCount { get; init; }

    public int AggregatedParagraphCount { get; init; }

    public static ScreenshotTranslationStageTimings Empty { get; } = new(
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        0,
        0);
}
