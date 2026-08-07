using System.Diagnostics;

namespace QuickTranslate.Core;

internal sealed record StreamingCompositionStats(
    int RequestedFrameCount,
    int PresentedFrameCount,
    int CoalescedRequestCount,
    double AverageWaitDurationMs,
    double MaxWaitDurationMs)
{
    public static StreamingCompositionStats Empty { get; } = new(0, 0, 0, 0, 0);
}

internal sealed class StreamingCompositionMetrics
{
    private readonly object _sync = new();
    private long _pendingStartedAt;
    private bool _hasPendingFrame;
    private int _requestedFrameCount;
    private int _presentedFrameCount;
    private int _coalescedRequestCount;
    private double _totalWaitDurationMs;
    private double _maxWaitDurationMs;

    public void RequestFrame()
    {
        lock (_sync)
        {
            _requestedFrameCount++;
            if (_hasPendingFrame)
            {
                _coalescedRequestCount++;
                return;
            }

            _pendingStartedAt = Stopwatch.GetTimestamp();
            _hasPendingFrame = true;
        }
    }

    public void RecordPresentedFrame()
    {
        lock (_sync)
        {
            if (!_hasPendingFrame)
                return;

            var waitDurationMs = Stopwatch.GetElapsedTime(_pendingStartedAt).TotalMilliseconds;
            _hasPendingFrame = false;
            _presentedFrameCount++;
            _totalWaitDurationMs += waitDurationMs;
            _maxWaitDurationMs = Math.Max(_maxWaitDurationMs, waitDurationMs);
        }
    }

    public StreamingCompositionStats GetStats()
    {
        lock (_sync)
        {
            return new StreamingCompositionStats(
                _requestedFrameCount,
                _presentedFrameCount,
                _coalescedRequestCount,
                _presentedFrameCount == 0 ? 0 : _totalWaitDurationMs / _presentedFrameCount,
                _maxWaitDurationMs);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _pendingStartedAt = 0;
            _hasPendingFrame = false;
            _requestedFrameCount = 0;
            _presentedFrameCount = 0;
            _coalescedRequestCount = 0;
            _totalWaitDurationMs = 0;
            _maxWaitDurationMs = 0;
        }
    }
}
