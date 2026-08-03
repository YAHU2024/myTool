using QuickTranslate.Models;

namespace QuickTranslate.Core;

public enum WordLookupSessionStatus
{
    Empty,
    Loading,
    Completed,
    NotFound,
    Failed,
    Cancelled
}

public sealed record WordLookupSessionState(
    long RequestId,
    string Query,
    WordLookupSessionStatus Status,
    WordLookupResult? Result,
    string? ErrorMessage);

public sealed record WordLookupRequestScope(long RequestId, CancellationToken Token);

public sealed class WordLookupSessionCoordinator : IDisposable
{
    private readonly object _sync = new();
    private long _requestId;
    private CancellationTokenSource? _activeSource;
    private WordLookupSessionState _state = new(0, string.Empty, WordLookupSessionStatus.Empty, null, null);

    public event Action<WordLookupSessionState>? StateChanged;

    public WordLookupSessionState Current
    {
        get { lock (_sync) return _state; }
    }

    public WordLookupRequestScope Begin(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        CancellationTokenSource? previous;
        WordLookupSessionState state;
        CancellationTokenSource source;
        lock (_sync)
        {
            previous = _activeSource;
            source = new CancellationTokenSource();
            _activeSource = source;
            state = _state = new(
                ++_requestId,
                query,
                WordLookupSessionStatus.Loading,
                null,
                null);
        }
        previous?.Cancel();
        StateChanged?.Invoke(state);
        return new WordLookupRequestScope(state.RequestId, source.Token);
    }

    public bool TryComplete(WordLookupRequestScope scope, WordLookupResult result) =>
        TryFinish(scope, WordLookupSessionStatus.Completed, result, null);

    public bool TryNotFound(WordLookupRequestScope scope) =>
        TryFinish(scope, WordLookupSessionStatus.NotFound, null, null);

    public bool TryFail(WordLookupRequestScope scope, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return TryFinish(scope, WordLookupSessionStatus.Failed, null, errorMessage);
    }

    public bool TryCancel(WordLookupRequestScope scope) =>
        TryFinish(scope, WordLookupSessionStatus.Cancelled, null, null);

    public bool TryReplaceCompletedResult(long requestId, WordLookupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        WordLookupSessionState state;
        lock (_sync)
        {
            if (_state.RequestId != requestId ||
                _state.Status != WordLookupSessionStatus.Completed ||
                _state.Result is null)
            {
                return false;
            }

            state = _state = _state with { Result = result };
        }
        StateChanged?.Invoke(state);
        return true;
    }

    public void CancelCurrent()
    {
        CancellationTokenSource? source;
        WordLookupSessionState? changed = null;
        lock (_sync)
        {
            source = _activeSource;
            _activeSource = null;
            _requestId++;
            if (_state.Status == WordLookupSessionStatus.Loading)
                changed = _state = _state with { Status = WordLookupSessionStatus.Cancelled };
        }
        source?.Cancel();
        source?.Dispose();
        if (changed is not null)
            StateChanged?.Invoke(changed);
    }

    private bool TryFinish(
        WordLookupRequestScope scope,
        WordLookupSessionStatus status,
        WordLookupResult? result,
        string? errorMessage)
    {
        WordLookupSessionState state;
        lock (_sync)
        {
            if (_activeSource is null ||
                scope.RequestId != _state.RequestId ||
                scope.RequestId != _requestId ||
                scope.Token.IsCancellationRequested ||
                _state.Status != WordLookupSessionStatus.Loading)
            {
                return false;
            }

            _activeSource.Dispose();
            _activeSource = null;
            state = _state = _state with
            {
                Status = status,
                Result = result,
                ErrorMessage = errorMessage
            };
        }
        StateChanged?.Invoke(state);
        return true;
    }

    public void Dispose()
    {
        CancelCurrent();
        lock (_sync)
        {
            _activeSource?.Dispose();
            _activeSource = null;
        }
    }
}
