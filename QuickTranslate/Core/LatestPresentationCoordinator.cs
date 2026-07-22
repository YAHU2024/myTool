namespace QuickTranslate.Core;

/// <summary>
/// Invalidates stale floating-window updates independently from API cancellation.
/// </summary>
internal sealed class LatestPresentationCoordinator
{
    private long _presentationId;

    public long Begin() => Interlocked.Increment(ref _presentationId);

    public bool IsCurrent(long presentationId) =>
        presentationId == Volatile.Read(ref _presentationId);
}
