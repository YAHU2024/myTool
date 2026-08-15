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

internal enum FloatingResultActiveOperationKind
{
    Root,
    FollowUp
}

internal readonly record struct FloatingResultActiveOperation(
    FloatingResultActiveOperationKind Kind,
    FloatingResultRequestIdentity? RootIdentity,
    AnalysisFollowUpRequestIdentity? FollowUpIdentity)
{
    public static FloatingResultActiveOperation Root(FloatingResultRequestIdentity identity) =>
        new(FloatingResultActiveOperationKind.Root, identity, null);

    public static FloatingResultActiveOperation FollowUp(AnalysisFollowUpRequestIdentity identity) =>
        new(FloatingResultActiveOperationKind.FollowUp, null, identity);
}

internal sealed record AnalysisFollowUpTransition(
    FloatingResultSession Session,
    AnalysisFollowUpRequestIdentity RequestIdentity,
    AnalysisFollowUpTurnState Turn);

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
    private FloatingResultActiveOperation? _activeOperation;

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

    public FloatingResultActiveOperation? ActiveOperation
    {
        get { lock (_sync) return _activeOperation; }
    }

    public FloatingResultSessionTransition StartSession(
        string sourceText,
        FloatingWindowAnchor? anchor,
        ContentType initialMode,
        DetectionResult? detection = null)
    {
        lock (_sync)
        {
            CancelActiveRequestLocked();
            _currentSession = new FloatingResultSession(Guid.NewGuid(), sourceText, anchor, initialMode, detection);
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
            if (IsReusableCompletedResult(state))
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
            if (_currentSession.ActiveMode == ContentType.Analysis)
                _currentSession.SetAnalysisConversation(AnalysisConversationState.Empty());
            return StartRequestLocked(_currentSession, _currentSession.ActiveMode);
        }
    }

    public FloatingResultSessionTransition RestoreCompletedMode(ContentType mode)
    {
        lock (_sync)
        {
            if (_currentSession is null || !IsReusableCompletedResult(_currentSession.GetModeState(mode)))
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
            if (_currentSession is not null && IsReusableCompletedResult(_currentSession.GetModeState(mode)))
            {
                state = _currentSession.GetModeState(mode);
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
            ErrorMessage = null,
            Quality = ModeResultQuality.Unassessed
        });

    public bool TryComplete(
        FloatingResultRequestIdentity identity,
        string rawText,
        AnalysisSemanticSnapshot? semanticSnapshot = null)
    {
        lock (_sync)
        {
            if (!CanApplyRootLocked(identity))
                return false;

            var state = _currentSession!.GetModeState(identity.Mode);
            _currentSession.SetModeState(identity.Mode, state with
            {
                Status = ModeResultStatus.Completed,
                RawText = rawText,
                ErrorMessage = null,
                Quality = ModeResultQuality.Normal
            });
            if (identity.Mode == ContentType.Analysis)
            {
                _currentSession.SetAnalysisConversation(new AnalysisConversationState(
                    identity.RequestId,
                    semanticSnapshot,
                    string.Empty,
                    Array.Empty<AnalysisFollowUpTurnState>()));
            }
            _activeOperation = null;
            return true;
        }
    }

    public bool TryFail(FloatingResultRequestIdentity identity, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return TryApply(identity, state => state with
        {
            Status = ModeResultStatus.Failed,
            ErrorMessage = errorMessage
        }, clearActiveRequest: true);
    }

    public bool TryCompleteWithEchoWarning(FloatingResultRequestIdentity identity, string rawText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawText);
        return TryApply(identity, state => state with
        {
            Status = ModeResultStatus.Completed,
            RawText = rawText,
            ErrorMessage = null,
            Quality = ModeResultQuality.EchoWarning
        }, clearActiveRequest: true);
    }

    public bool TryCancel(FloatingResultRequestIdentity identity) =>
        TryApply(identity, state => state with { Status = ModeResultStatus.Cancelled }, clearActiveRequest: true);

    public AnalysisFollowUpTransition BeginFollowUp(string question)
    {
        lock (_sync)
        {
            var session = RequireFollowUpReadySessionLocked();
            var conversation = session.AnalysisConversation;
            if (conversation.Turns.Count >= 10)
                throw new InvalidOperationException("已达到本次解析的 10 轮追问上限");

            CancelActiveRequestLocked();
            var normalizedQuestion = AnalysisConversationFormatter.NormalizeQuestion(question);

            var identity = NewFollowUpIdentityLocked(session, conversation, conversation.Turns.Count + 1);
            var turn = new AnalysisFollowUpTurnState(
                identity.TurnNumber,
                normalizedQuestion,
                string.Empty,
                AnalysisFollowUpTurnStatus.Loading,
                identity.RequestId);
            session.SetAnalysisConversation(conversation with
            {
                Draft = string.Empty,
                Turns = conversation.Turns.Append(turn).ToArray()
            });
            _activeOperation = FloatingResultActiveOperation.FollowUp(identity);
            return new AnalysisFollowUpTransition(session, identity, turn);
        }
    }

    public AnalysisFollowUpTransition RetryLatestFollowUp()
    {
        lock (_sync)
        {
            var session = RequireFollowUpReadySessionLocked();
            var conversation = session.AnalysisConversation;
            var turn = conversation.Turns.LastOrDefault()
                ?? throw new InvalidOperationException("没有可重试的追问");
            if (turn.Status is not (AnalysisFollowUpTurnStatus.Failed or AnalysisFollowUpTurnStatus.Cancelled))
                throw new InvalidOperationException("只有最新未完成追问可以重试");

            CancelActiveRequestLocked();
            var identity = NewFollowUpIdentityLocked(session, conversation, turn.TurnNumber);
            var replacement = turn with
            {
                AnswerRawText = string.Empty,
                Status = AnalysisFollowUpTurnStatus.Loading,
                LastRequestId = identity.RequestId
            };
            session.SetAnalysisConversation(conversation with
            {
                Turns = conversation.Turns.Take(conversation.Turns.Count - 1).Append(replacement).ToArray()
            });
            _activeOperation = FloatingResultActiveOperation.FollowUp(identity);
            return new AnalysisFollowUpTransition(session, identity, replacement);
        }
    }

    public bool TryUpdateFollowUpStreaming(AnalysisFollowUpRequestIdentity identity, string rawText) =>
        TryApplyFollowUp(identity, turn => turn with
        {
            AnswerRawText = rawText,
            Status = AnalysisFollowUpTurnStatus.Loading
        });

    public bool TryCompleteFollowUp(AnalysisFollowUpRequestIdentity identity, string rawText) =>
        TryApplyFollowUp(identity, turn => turn with
        {
            AnswerRawText = rawText,
            Status = AnalysisFollowUpTurnStatus.Completed
        }, clearActiveRequest: true);

    public bool TryFailFollowUp(AnalysisFollowUpRequestIdentity identity) =>
        TryApplyFollowUp(identity, turn => turn with
        {
            Status = AnalysisFollowUpTurnStatus.Failed
        }, clearActiveRequest: true);

    public bool TryCancelFollowUp(AnalysisFollowUpRequestIdentity identity) =>
        TryApplyFollowUp(identity, turn => turn with
        {
            Status = AnalysisFollowUpTurnStatus.Cancelled
        }, clearActiveRequest: true);

    public bool CancelActiveFollowUp()
    {
        lock (_sync)
        {
            if (_activeOperation is not { Kind: FloatingResultActiveOperationKind.FollowUp })
                return false;
            CancelActiveRequestLocked();
            return true;
        }
    }

    public bool TrySetAnalysisDraft(Guid sessionId, string draft)
    {
        lock (_sync)
        {
            if (_currentSession?.SessionId != sessionId)
                return false;
            _currentSession.SetAnalysisConversation(_currentSession.AnalysisConversation with { Draft = draft });
            return true;
        }
    }

    public IReadOnlyList<AnalysisFollowUpExchange> GetCompletedFollowUpExchanges(Guid sessionId)
    {
        lock (_sync)
        {
            if (_currentSession?.SessionId != sessionId)
                return Array.Empty<AnalysisFollowUpExchange>();
            return _currentSession.AnalysisConversation.Turns
                .Where(turn => turn.Status == AnalysisFollowUpTurnStatus.Completed)
                .Select(turn => new AnalysisFollowUpExchange(turn.Question, turn.AnswerRawText))
                .ToArray();
        }
    }

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
            Quality = ModeResultQuality.Unassessed,
            LastRequestId = identity.RequestId,
            ScrollOffset = 0,
            AutoScrollEnabled = true
        });
        _activeOperation = FloatingResultActiveOperation.Root(identity);
        return new(FloatingResultSessionTransitionKind.StartedRequest, session, identity);
    }

    private void CancelActiveRequestLocked()
    {
        if (_activeOperation is not { } operation || _currentSession is null)
        {
            _activeOperation = null;
            return;
        }

        if (operation.Kind == FloatingResultActiveOperationKind.Root && operation.RootIdentity is { } rootIdentity)
        {
            if (_currentSession.SessionId == rootIdentity.SessionId)
            {
                var state = _currentSession.GetModeState(rootIdentity.Mode);
                if (state.Status == ModeResultStatus.Loading && state.LastRequestId == rootIdentity.RequestId)
                    _currentSession.SetModeState(rootIdentity.Mode, state with { Status = ModeResultStatus.Cancelled });
            }
        }
        else if (operation.FollowUpIdentity is { } followUpIdentity &&
                 _currentSession.SessionId == followUpIdentity.SessionId)
        {
            UpdateFollowUpTurnLocked(followUpIdentity, turn => turn with
            {
                Status = AnalysisFollowUpTurnStatus.Cancelled
            });
        }

        _activeOperation = null;
    }

    private bool TryApply(
        FloatingResultRequestIdentity identity,
        Func<ModeResultState, ModeResultState> update,
        bool clearActiveRequest = false)
    {
        lock (_sync)
        {
            if (!CanApplyRootLocked(identity))
            {
                return false;
            }

            var session = _currentSession!;
            var state = session.GetModeState(identity.Mode);
            if (state.Status != ModeResultStatus.Loading || state.LastRequestId != identity.RequestId)
                return false;

            session.SetModeState(identity.Mode, update(state));
            if (clearActiveRequest)
                _activeOperation = null;
            return true;
        }
    }

    private bool CanApplyRootLocked(FloatingResultRequestIdentity identity) =>
        _currentSession?.SessionId == identity.SessionId &&
        _activeOperation is
        {
            Kind: FloatingResultActiveOperationKind.Root,
            RootIdentity: { } activeIdentity
        } &&
        activeIdentity == identity &&
        _presentationId == identity.PresentationId &&
        _currentSession.GetModeState(identity.Mode) is
        {
            Status: ModeResultStatus.Loading,
            LastRequestId: { } lastRequestId
        } &&
        lastRequestId == identity.RequestId;

    private static bool IsReusableCompletedResult(ModeResultState state) =>
        state.Status == ModeResultStatus.Completed && state.Quality != ModeResultQuality.EchoWarning;

    private FloatingResultSession RequireFollowUpReadySessionLocked()
    {
        var session = _currentSession ?? throw new InvalidOperationException("当前没有结果会话");
        if (session.ActiveMode != ContentType.Analysis ||
            session.GetModeState(ContentType.Analysis).Status != ModeResultStatus.Completed ||
            session.AnalysisConversation is not
            {
                RootAnalysisRequestId: { },
                SemanticSnapshot: not null
            })
        {
            throw new InvalidOperationException("当前解析结果不能追问");
        }
        return session;
    }

    private AnalysisFollowUpRequestIdentity NewFollowUpIdentityLocked(
        FloatingResultSession session,
        AnalysisConversationState conversation,
        int turnNumber) => new(
            session.SessionId,
            conversation.RootAnalysisRequestId!.Value,
            turnNumber,
            ++_requestId,
            _presentationId);

    private bool TryApplyFollowUp(
        AnalysisFollowUpRequestIdentity identity,
        Func<AnalysisFollowUpTurnState, AnalysisFollowUpTurnState> update,
        bool clearActiveRequest = false)
    {
        lock (_sync)
        {
            if (_currentSession?.SessionId != identity.SessionId ||
                _currentSession.ActiveMode != ContentType.Analysis ||
                _currentSession.AnalysisConversation.RootAnalysisRequestId != identity.RootAnalysisRequestId ||
                _activeOperation is not
                {
                    Kind: FloatingResultActiveOperationKind.FollowUp,
                    FollowUpIdentity: { } activeIdentity
                } ||
                activeIdentity != identity ||
                _presentationId != identity.PresentationId ||
                !UpdateFollowUpTurnLocked(identity, update))
            {
                return false;
            }

            if (clearActiveRequest)
                _activeOperation = null;
            return true;
        }
    }

    private bool UpdateFollowUpTurnLocked(
        AnalysisFollowUpRequestIdentity identity,
        Func<AnalysisFollowUpTurnState, AnalysisFollowUpTurnState> update)
    {
        if (_currentSession is null)
            return false;
        var conversation = _currentSession.AnalysisConversation;
        var index = conversation.Turns.Count - 1;
        if (index < 0)
            return false;
        var turn = conversation.Turns[index];
        if (turn.TurnNumber != identity.TurnNumber ||
            turn.LastRequestId != identity.RequestId ||
            turn.Status != AnalysisFollowUpTurnStatus.Loading)
        {
            return false;
        }

        var turns = conversation.Turns.ToArray();
        turns[index] = update(turn);
        _currentSession.SetAnalysisConversation(conversation with { Turns = turns });
        return true;
    }
}
