using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class StreamingRuntimeMetricsTests
{
    [Fact]
    public void Since_ReturnsNonNegativeRuntimeDeltas()
    {
        var start = new StreamingRuntimeStats(3, 2, 1, 10.5, 1_000);
        var end = new StreamingRuntimeStats(5, 2, 2, 13.75, 1_640);

        var result = end.Since(start);

        Assert.Equal(2, result.Gen0Collections);
        Assert.Equal(0, result.Gen1Collections);
        Assert.Equal(1, result.Gen2Collections);
        Assert.Equal(3.25, result.GcPauseDurationMs);
        Assert.Equal(640, result.AllocatedBytes);
    }

    [Fact]
    public void Since_ClampsResetOrUnsupportedCountersToZero()
    {
        var start = new StreamingRuntimeStats(3, 2, 1, 10.5, 1_000);
        var end = StreamingRuntimeStats.Empty;

        var result = end.Since(start);

        Assert.Equal(StreamingRuntimeStats.Empty, result);
    }
}
