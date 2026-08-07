namespace QuickTranslate.Core;

internal sealed record StreamingRuntimeStats(
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double GcPauseDurationMs,
    long AllocatedBytes)
{
    public static StreamingRuntimeStats Empty { get; } = new(0, 0, 0, 0, 0);

    public static StreamingRuntimeStats Capture() => new(
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        GetTotalPauseDurationMs(),
        GC.GetTotalAllocatedBytes(precise: false));

    public StreamingRuntimeStats Since(StreamingRuntimeStats start) => new(
        Math.Max(0, Gen0Collections - start.Gen0Collections),
        Math.Max(0, Gen1Collections - start.Gen1Collections),
        Math.Max(0, Gen2Collections - start.Gen2Collections),
        Math.Max(0, GcPauseDurationMs - start.GcPauseDurationMs),
        Math.Max(0, AllocatedBytes - start.AllocatedBytes));

    private static double GetTotalPauseDurationMs()
    {
        try
        {
            return GC.GetTotalPauseDuration().TotalMilliseconds;
        }
        catch (PlatformNotSupportedException)
        {
            return 0;
        }
    }
}
