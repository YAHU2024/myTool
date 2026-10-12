namespace QuickTranslate.Core;

/// <summary>
/// Privacy-safe qualification counters for one screenshot translation.
/// It stores counts and enum reasons only; OCR text is never retained.
/// </summary>
public sealed class ScreenshotTranslationDiagnostics
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string AttemptId { get; } = Guid.NewGuid().ToString("N");
    private int _cancellationReason = (int)ScreenshotTranslationCancellationReason.Unknown;

    public ScreenshotTranslationDiagnostics(string? sessionId = null)
    {
        if (sessionId is not null)
            SessionId = sessionId;
    }

    public ScreenshotTranslationCancellationReason CancellationReason =>
        (ScreenshotTranslationCancellationReason)Volatile.Read(ref _cancellationReason);

    public bool TryRecordCancellation(ScreenshotTranslationCancellationReason reason) =>
        reason != ScreenshotTranslationCancellationReason.Unknown &&
        Interlocked.CompareExchange(ref _cancellationReason, (int)reason,
            (int)ScreenshotTranslationCancellationReason.Unknown) == (int)ScreenshotTranslationCancellationReason.Unknown;

    private readonly object _sync = new();
    private readonly Dictionary<ScreenshotTranslationEligibilityReason, int> _reasonCounts = new();
    private int _evaluatedCount;
    private int _translateCount;
    private int _preserveCount;
    private int _skipCount;

    public ScreenshotTranslationEligibility Evaluate(string text, string targetLanguage)
    {
        var result = ScreenshotTranslationEligibilityEvaluator.Evaluate(text, targetLanguage);
        lock (_sync)
        {
            _evaluatedCount++;
            switch (result.Action)
            {
                case ScreenshotTranslationAction.Translate:
                    _translateCount++;
                    break;
                case ScreenshotTranslationAction.Preserve:
                    _preserveCount++;
                    break;
                case ScreenshotTranslationAction.Skip:
                    _skipCount++;
                    break;
            }

            _reasonCounts[result.Reason] = _reasonCounts.GetValueOrDefault(result.Reason) + 1;
        }

        return result;
    }

    public ScreenshotTranslationDiagnosticsSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new(
                _evaluatedCount,
                _translateCount,
                _preserveCount,
                _skipCount,
                _reasonCounts.ToDictionary(
                    pair => pair.Key.ToString(),
                    pair => pair.Value,
                    StringComparer.Ordinal));
        }
    }
}

public sealed record ScreenshotTranslationDiagnosticsSnapshot(
    int EvaluatedCount,
    int TranslateCount,
    int PreserveCount,
    int SkipCount,
    IReadOnlyDictionary<string, int> ReasonCounts);
