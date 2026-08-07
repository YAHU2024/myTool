using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class StreamingDispatcherMetricsTests
{
    [Fact]
    public void Record_AggregatesQueueAndExecutionDurations()
    {
        var metrics = new StreamingDispatcherMetrics();

        metrics.Record(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(4));
        metrics.Record(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(8));

        var stats = metrics.GetStats();
        Assert.Equal(2, stats.FrameCount);
        Assert.Equal(20, stats.AverageQueueDelayMs);
        Assert.Equal(30, stats.MaxQueueDelayMs);
        Assert.Equal(6, stats.AverageExecutionDurationMs);
        Assert.Equal(8, stats.MaxExecutionDurationMs);
    }
}
