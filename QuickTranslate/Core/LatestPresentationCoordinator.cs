namespace QuickTranslate.Core;

/// <summary>
/// Invalidates stale floating-window updates independently from API cancellation.
/// </summary>
internal sealed class LatestPresentationCoordinator
{
    private long _presentationId;

    public long Begin() => Interlocked.Increment(ref _presentationId);

    public long Begin(long presentationId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(presentationId);
        Interlocked.Exchange(ref _presentationId, presentationId);
        return presentationId;
    }

    public bool IsCurrent(long presentationId) =>
        presentationId == Volatile.Read(ref _presentationId);
}
