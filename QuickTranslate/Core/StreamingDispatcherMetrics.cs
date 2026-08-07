using System.Windows.Threading;

namespace QuickTranslate.Core;

internal sealed record StreamingDispatcherStats(
    int FrameCount,
    double AverageQueueDelayMs,
    double MaxQueueDelayMs,
    double AverageExecutionDurationMs,
    double MaxExecutionDurationMs);

internal sealed class StreamingDispatcherMetrics
{
    internal static DispatcherPriority PresentationPriority => DispatcherPriority.Render;

    private readonly object _sync = new();
    private int _frameCount;
    private double _totalQueueDelayMs;
    private double _maxQueueDelayMs;
    private double _totalExecutionDurationMs;
    private double _maxExecutionDurationMs;

    public void Record(TimeSpan queueDelay, TimeSpan executionDuration)
    {
        lock (_sync)
        {
            _frameCount++;
            _totalQueueDelayMs += queueDelay.TotalMilliseconds;
            _maxQueueDelayMs = Math.Max(_maxQueueDelayMs, queueDelay.TotalMilliseconds);
            _totalExecutionDurationMs += executionDuration.TotalMilliseconds;
            _maxExecutionDurationMs = Math.Max(_maxExecutionDurationMs, executionDuration.TotalMilliseconds);
        }
    }

    public StreamingDispatcherStats GetStats()
    {
        lock (_sync)
        {
            return new StreamingDispatcherStats(
                _frameCount,
                _frameCount == 0 ? 0 : _totalQueueDelayMs / _frameCount,
                _maxQueueDelayMs,
                _frameCount == 0 ? 0 : _totalExecutionDurationMs / _frameCount,
                _maxExecutionDurationMs);
        }
    }
}
