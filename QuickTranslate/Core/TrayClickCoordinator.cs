namespace QuickTranslate.Core;

public readonly record struct PhysicalPoint(int X, int Y);

public sealed record TrayClickSnapshot(
    long Sequence,
    bool WasLookupVisible,
    PhysicalPoint Anchor);

public enum TrayClickActionKind
{
    NoOp,
    ShowLookup,
    HideLookup,
    OpenSettings,
    HideForDeactivation
}

public sealed record TrayClickAction(
    TrayClickActionKind Kind,
    TrayClickSnapshot? Snapshot = null);

public sealed class TrayClickCoordinator : IDisposable
{
    private readonly object _sync = new();
    private long _sequence;
    private TrayClickSnapshot? _pending;
    private bool _disposed;

    public TrayClickSnapshot RecordLeftButtonDown(bool lookupVisible, PhysicalPoint anchor)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pending = new TrayClickSnapshot(++_sequence, lookupVisible, anchor);
        }
    }

    public TrayClickAction ConfirmSingleClick(long sequence)
    {
        lock (_sync)
        {
            if (_disposed || _pending is not { } snapshot || snapshot.Sequence != sequence)
                return new(TrayClickActionKind.NoOp);

            _pending = null;
            return snapshot.WasLookupVisible
                ? new(TrayClickActionKind.HideLookup, snapshot)
                : new(TrayClickActionKind.ShowLookup, snapshot);
        }
    }

    public TrayClickAction RecordDoubleClick()
    {
        lock (_sync)
        {
            if (_disposed)
                return new(TrayClickActionKind.NoOp);
            _pending = null;
            return new(TrayClickActionKind.OpenSettings);
        }
    }

    public TrayClickAction RecordDeactivated()
    {
        lock (_sync)
        {
            if (_disposed || _pending is not null)
                return new(TrayClickActionKind.NoOp);
            return new(TrayClickActionKind.HideForDeactivation);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _pending = null;
        }
    }
}
