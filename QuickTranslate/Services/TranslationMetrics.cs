namespace QuickTranslate.Services;

public sealed record TranslationMetricsSnapshot(
    DateTime Date,
    long Completed,
    long Failed,
    long Cancelled,
    long Expired,
    long CacheHits,
    double CacheHitRate,
    double AverageMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds);

/// <summary>
/// Process-local translation metrics. It intentionally has no persistence or user content.
/// </summary>
public sealed class TranslationMetrics
{
    private readonly object _sync = new();
    private readonly Queue<double> _durations = new();
    private readonly int _windowSize;
    private DateTime _date = DateTime.Today;
    private long _completed;
    private long _failed;
    private long _cancelled;
    private long _expired;
    private long _cacheHits;
    private long _cacheLookups;

    public TranslationMetrics(int windowSize = 100)
    {
        if (windowSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSize));
        _windowSize = windowSize;
    }

    public void RecordCompleted(TimeSpan duration, bool cacheHit = false)
    {
        lock (_sync)
        {
            ResetIfDateChanged();
            _completed++;
            _cacheLookups++;
            if (cacheHit)
                _cacheHits++;
            if (!cacheHit)
            {
                _durations.Enqueue(Math.Max(0, duration.TotalMilliseconds));
                while (_durations.Count > _windowSize)
                    _durations.Dequeue();
            }
        }
    }

    public void RecordCacheMiss() { lock (_sync) { ResetIfDateChanged(); _cacheLookups++; } }
    public void RecordFailed() { lock (_sync) { ResetIfDateChanged(); _failed++; } }
    public void RecordCancelled() { lock (_sync) { ResetIfDateChanged(); _cancelled++; } }
    public void RecordExpired() { lock (_sync) { ResetIfDateChanged(); _expired++; } }

    public TranslationMetricsSnapshot GetSnapshot(long cacheHits, long cacheMisses)
    {
        lock (_sync)
        {
            ResetIfDateChanged();
            var values = _durations.OrderBy(value => value).ToArray();
            return new TranslationMetricsSnapshot(
                _date,
                _completed,
                _failed,
                _cancelled,
                _expired,
                cacheHits,
                cacheHits + cacheMisses == 0 ? 0 : (double)cacheHits / (cacheHits + cacheMisses),
                values.Length == 0 ? 0 : values.Average(),
                Percentile(values, 0.50),
                Percentile(values, 0.95),
                Percentile(values, 0.99));
        }
    }

    private void ResetIfDateChanged()
    {
        if (DateTime.Today == _date)
            return;
        _date = DateTime.Today;
        _completed = 0;
        _failed = 0;
        _cancelled = 0;
        _expired = 0;
        _cacheHits = 0;
        _cacheLookups = 0;
        _durations.Clear();
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        var index = (values.Count - 1) * percentile;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
            return values[lower];
        var fraction = index - lower;
        return values[lower] + (values[upper] - values[lower]) * fraction;
    }
}
