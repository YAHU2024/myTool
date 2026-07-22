namespace QuickTranslate.Core;

/// <summary>
/// Owns the latest-request-wins identity and cancellation lifecycle.
/// </summary>
internal sealed class LatestRequestCoordinator
{
    private readonly object _sync = new();
    private long _requestId;
    private CancellationTokenSource? _activeSource;

    public RequestScope Begin()
    {
        var source = new CancellationTokenSource();
        CancellationTokenSource? previous;
        long requestId;

        lock (_sync)
        {
            previous = _activeSource;
            _activeSource = source;
            requestId = ++_requestId;
        }

        previous?.Cancel();
        return new RequestScope(requestId, source);
    }

    public bool IsCurrent(RequestScope scope)
    {
        lock (_sync)
        {
            return scope.RequestId == _requestId &&
                   ReferenceEquals(scope.CancellationSource, _activeSource) &&
                   !scope.Token.IsCancellationRequested;
        }
    }

    public void Complete(RequestScope scope)
    {
        lock (_sync)
        {
            if (ReferenceEquals(scope.CancellationSource, _activeSource))
                _activeSource = null;
        }

        scope.CancellationSource.Dispose();
    }

    public void Cancel()
    {
        CancellationTokenSource? active;
        lock (_sync)
        {
            active = _activeSource;
            _activeSource = null;
            _requestId++;
        }

        active?.Cancel();
    }

    internal sealed record RequestScope(
        long RequestId,
        CancellationTokenSource CancellationSource)
    {
        public CancellationToken Token => CancellationSource.Token;
    }
}
