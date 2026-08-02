using QuickTranslate.Services;

namespace QuickTranslate.Core;

public enum TtsPlaybackOwner
{
    FloatingResult,
    QuickLookup
}

public sealed record TtsPlaybackState(
    TtsPlaybackOwner? Owner,
    long OperationId,
    bool IsBusy);

public sealed class TtsPlaybackCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly ITtsService _service;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private long _operationId;
    private CancellationTokenSource? _activeSource;
    private TtsPlaybackState _state = new(null, 0, false);
    private bool _disposed;

    public TtsPlaybackCoordinator(ITtsService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public event Action<TtsPlaybackState>? StateChanged;

    public TtsPlaybackState Current
    {
        get { lock (_sync) return _state; }
    }

    public bool IsBusy(TtsPlaybackOwner owner)
    {
        var state = Current;
        return state.IsBusy && state.Owner == owner;
    }

    public async Task SpeakAsync(
        TtsPlaybackOwner owner,
        string text,
        string? languageHint,
        string? voiceOverride,
        double rate,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancellationTokenSource linked;
        CancellationTokenSource? previous;
        TtsPlaybackState state;
        lock (_sync)
        {
            previous = _activeSource;
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeSource = linked;
            state = _state = new(owner, ++_operationId, true);
        }
        previous?.Cancel();
        StateChanged?.Invoke(state);

        try
        {
            Task playback;
            var gateAcquired = false;
            try
            {
                await _transitionGate.WaitAsync(linked.Token).ConfigureAwait(false);
                gateAcquired = true;
                if (!IsCurrent(state.OperationId, linked))
                    throw new OperationCanceledException(linked.Token);
                await _service.StopAsync().ConfigureAwait(false);
                if (!IsCurrent(state.OperationId, linked))
                    throw new OperationCanceledException(linked.Token);
                playback = _service.SpeakAsync(text, languageHint, voiceOverride, rate, linked.Token);
            }
            finally
            {
                if (gateAcquired)
                    _transitionGate.Release();
            }

            try
            {
                await playback.ConfigureAwait(false);
            }
            catch (TtsSpeakException ex)
                when (linked.IsCancellationRequested || ex.ErrorKind == TtsSpeakException.Cancelled)
            {
                throw new OperationCanceledException("Speech was cancelled.", ex, linked.Token);
            }
        }
        finally
        {
            CompleteIfCurrent(state.OperationId, linked);
        }
    }

    public async Task StopAsync(TtsPlaybackOwner owner)
    {
        long operationId;
        CancellationTokenSource? source;
        TtsPlaybackState? changed = null;
        lock (_sync)
        {
            if (!_state.IsBusy || _state.Owner != owner)
                return;
            operationId = _state.OperationId;
            source = _activeSource;
            _activeSource = null;
            changed = _state = new(null, operationId, false);
        }
        source?.Cancel();
        StateChanged?.Invoke(changed);

        await _transitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _service.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public string? TakeLastUiHint() =>
        _service is EdgeTtsService edgeTts ? edgeTts.TakeLastUiHint() : null;

    private bool IsCurrent(long operationId, CancellationTokenSource source)
    {
        lock (_sync)
        {
            return !_disposed &&
                   _state.IsBusy &&
                   _state.OperationId == operationId &&
                   ReferenceEquals(_activeSource, source);
        }
    }

    private void CompleteIfCurrent(long operationId, CancellationTokenSource source)
    {
        TtsPlaybackState? changed = null;
        lock (_sync)
        {
            if (_state.OperationId == operationId && ReferenceEquals(_activeSource, source))
            {
                _activeSource = null;
                changed = _state = new(null, operationId, false);
            }
        }
        source.Dispose();
        if (changed is not null)
            StateChanged?.Invoke(changed);
    }

    public void Dispose()
    {
        CancellationTokenSource? source;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            source = _activeSource;
            _activeSource = null;
            _state = new(null, _operationId, false);
        }
        source?.Cancel();
        source?.Dispose();
        // A cancelled SpeakAsync may still be unwinding through the gate.
    }
}
