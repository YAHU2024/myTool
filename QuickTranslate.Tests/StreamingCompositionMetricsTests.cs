using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class StreamingCompositionMetricsTests
{
    [Fact]
    public void PresentedFrame_CoalescesPendingRequestsAndRecordsWait()
    {
        var metrics = new StreamingCompositionMetrics();

        metrics.RequestFrame();
        metrics.RequestFrame();
        metrics.RecordPresentedFrame();

        var stats = metrics.GetStats();
        Assert.Equal(2, stats.RequestedFrameCount);
        Assert.Equal(1, stats.PresentedFrameCount);
        Assert.Equal(1, stats.CoalescedRequestCount);
        Assert.True(stats.AverageWaitDurationMs >= 0);
        Assert.True(stats.MaxWaitDurationMs >= stats.AverageWaitDurationMs);
    }

    [Fact]
    public void Reset_ClearsCompletedAndPendingSamples()
    {
        var metrics = new StreamingCompositionMetrics();
        metrics.RequestFrame();
        metrics.Reset();
        metrics.RecordPresentedFrame();

        Assert.Equal(StreamingCompositionStats.Empty, metrics.GetStats());
    }
}
