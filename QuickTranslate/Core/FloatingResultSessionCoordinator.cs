using QuickTranslate.Models;
using QuickTranslate.UI;

namespace QuickTranslate.Core;

internal enum FloatingResultSessionTransitionKind
{
    StartedRequest,
    RestoredCompleted,
    NoOp,
    Dismissed
}

/// <summary>
/// The result of a session operation. A non-null request identity authorizes exactly one request.
/// </summary>
internal sealed record FloatingResultSessionTransition(
    FloatingResultSessionTransitionKind Kind,
    FloatingResultSession? Session,
    FloatingResultRequestIdentity? RequestIdentity);

/// <summary>
/// Owns the session, request and presentation identities for floating results.
/// It is deliberately transport-agnostic: callers use transitions to start/cancel HTTP work,
/// while all asynchronous result callbacks are accepted only through identity-checked methods.
/// </summary>
internal sealed class FloatingResultSessionCoordinator
{
    private readonly object _sync = new();
    private long _requestId;
    private long _presentationId;
    private FloatingResultSession? _currentSession;
    private FloatingResultRequestIdentity? _activeRequest;

    public Guid? CurrentSessionId
    {
        get { lock (_sync) return _currentSession?.SessionId; }
    }

    public long CurrentRequestId
    {
        get { lock (_sync) return _requestId; }
    }

    public long CurrentPresentationId
    {
        get { lock (_sync) return _presentationId; }
    }

    public FloatingResultSession? CurrentSession
    {
        get { lock (_sync) return _currentSession; }
    }

    public FloatingResultSessionTransition StartSession(
        string sourceText,
        FloatingWindowAnchor? anchor,
        ContentType initialMode)
    {
        lock (_sync)
        {
            CancelActiveRequestLocked();
            _currentSession = new FloatingResultSession(Guid.NewGuid(), sourceText, anchor, initialMode);
            return StartRequestLocked(_currentSession, initialMode);
        }
    }

    public FloatingResultSessionTransition StartSession(string sourceText, ContentType initialMode) =>
        StartSession(sourceText, anchor: null, initialMode);

    /// <summary>
    /// Selects a mode. It restores a completed result or starts one request for all other states.
    /// </summary>
    public FloatingResultSessionTransition BeginRequest(ContentType mode) => SwitchMode(mode);

    public FloatingResultSessionTransition SwitchMode(ContentType mode)
    {
        lock (_sync)
        {
            if (_currentSession is null)
                return new(FloatingResultSessionTransitionKind.NoOp, null, null);

            if (_currentSession.ActiveMode == mode)
                return new(FloatingResultSessionTransitionKind.NoOp, _currentSession, null);

            CancelActiveRequestLocked();
            _currentSession.SetActiveMode(mode);
            var state = _currentSession.GetModeState(mode);
            if (state.Status == ModeResultStatus.Completed)
            {
                _presentationId++;
                return new(FloatingResultSessionTransitionKind.RestoredCompleted, _currentSession, null);
            }

            return StartRequestLocked(_currentSession, mode);
        }
    }

    public FloatingResultSessionTransition RefreshMode()
    {
        lock (_sync)
        {
            if (_currentSession is null)
                return new(FloatingResultSessionTransitionKind.NoOp, null, null);

            CancelActiveRequestLocked();
            return StartRequestLocked(_currentSession, _currentSession.ActiveMode);
        }
    }

    public FloatingResultSessionTransition RestoreCompletedMode(ContentType mode)
    {
        lock (_sync)
        {
            if (_currentSession is null || _currentSession.GetModeState(mode).Status != ModeResultStatus.Completed)
                return new(FloatingResultSessionTransitionKind.NoOp, _currentSession, null);

            CancelActiveRequestLocked();
            _currentSession.SetActiveMode(mode);
            _presentationId++;
            return new(FloatingResultSessionTransitionKind.RestoredCompleted, _currentSession, null);
        }
    }

    public bool TryGetCompletedMode(ContentType mode, out ModeResultState? state)
    {
        lock (_sync)
        {
            if (_currentSession is not null && _currentSession.GetModeState(mode) is { Status: ModeResultStatus.Completed } completed)
            {
                state = completed;
                return true;
            }

            state = null;
            return false;
        }
    }

    public FloatingResultSessionTransition DismissSession()
    {
        lock (_sync)
        {
            CancelActiveRequestLocked();
            _currentSession = null;
            _presentationId++;
            return new(FloatingResultSessionTransitionKind.Dismissed, null, null);
        }
    }

    public void CancelActiveRequest()
    {
        lock (_sync)
        {
            CancelActiveRequestLocked();
        }
    }

    public bool TryUpdateStreaming(FloatingResultRequestIdentity identity, string rawText) =>
        TryApply(identity, state => state with
        {
            Status = ModeResultStatus.Loading,
            RawText = rawText,
            ErrorMessage = null
        });

    public bool TryComplete(FloatingResultRequestIdentity identity, string rawText) =>
        TryApply(identity, state => state with
        {
            Status = ModeResultStatus.Completed,
            RawText = rawText,
            ErrorMessage = null
        }, clearActiveRequest: true);

    public bool TryFail(FloatingResultRequestIdentity identity, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return TryApply(identity, state => state with
        {
            Status = ModeResultStatus.Failed,
            ErrorMessage = errorMessage
        }, clearActiveRequest: true);
    }

    public bool TryCancel(FloatingResultRequestIdentity identity) =>
        TryApply(identity, state => state with { Status = ModeResultStatus.Cancelled }, clearActiveRequest: true);

    public bool TrySetScrollState(Guid sessionId, ContentType mode, double scrollOffset, bool autoScrollEnabled)
    {
        if (scrollOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(scrollOffset));

        lock (_sync)
        {
            if (_currentSession?.SessionId != sessionId)
                return false;

            var state = _currentSession.GetModeState(mode);
            _currentSession.SetModeState(mode, state with
            {
                ScrollOffset = scrollOffset,
                AutoScrollEnabled = autoScrollEnabled
            });
            return true;
        }
    }

    private FloatingResultSessionTransition StartRequestLocked(FloatingResultSession session, ContentType mode)
    {
        var identity = new FloatingResultRequestIdentity(
            session.SessionId,
            mode,
            ++_requestId,
            ++_presentationId);
        var previous = session.GetModeState(mode);
        session.SetModeState(mode, previous with
        {
            Status = ModeResultStatus.Loading,
            RawText = string.Empty,
            ErrorMessage = null,
            LastRequestId = identity.RequestId,
            ScrollOffset = 0,
            AutoScrollEnabled = true
        });
        _activeRequest = identity;
        return new(FloatingResultSessionTransitionKind.StartedRequest, session, identity);
    }

    private void CancelActiveRequestLocked()
    {
        if (_activeRequest is not { } identity || _currentSession is null || _currentSession.SessionId != identity.SessionId)
        {
            _activeRequest = null;
            return;
        }

        var state = _currentSession.GetModeState(identity.Mode);
        if (state.Status == ModeResultStatus.Loading && state.LastRequestId == identity.RequestId)
            _currentSession.SetModeState(identity.Mode, state with { Status = ModeResultStatus.Cancelled });

        _activeRequest = null;
    }

    private bool TryApply(
        FloatingResultRequestIdentity identity,
        Func<ModeResultState, ModeResultState> update,
        bool clearActiveRequest = false)
    {
        lock (_sync)
        {
            if (_currentSession?.SessionId != identity.SessionId ||
                _activeRequest != identity ||
                _presentationId != identity.PresentationId)
            {
                return false;
            }

            var state = _currentSession.GetModeState(identity.Mode);
            if (state.Status != ModeResultStatus.Loading || state.LastRequestId != identity.RequestId)
                return false;

            _currentSession.SetModeState(identity.Mode, update(state));
            if (clearActiveRequest)
                _activeRequest = null;
            return true;
        }
    }
}
