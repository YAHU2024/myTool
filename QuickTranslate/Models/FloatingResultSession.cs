using System.Collections.ObjectModel;
using QuickTranslate.Core;
using QuickTranslate.UI;

namespace QuickTranslate.Models;

/// <summary>
/// The lifecycle state of one result mode within a floating-result session.
/// </summary>
internal enum ModeResultStatus
{
    NotStarted,
    Loading,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Immutable, read-only view of a single mode's result state.
/// Instances are replaced by <see cref="FloatingResultSessionCoordinator"/> on transitions.
/// </summary>
internal sealed record ModeResultState(
    ModeResultStatus Status,
    string RawText,
    string? ErrorMessage,
    long? LastRequestId,
    double ScrollOffset,
    bool AutoScrollEnabled)
{
    internal static ModeResultState NotStarted() =>
        new(ModeResultStatus.NotStarted, string.Empty, null, null, 0, true);
}

/// <summary>
/// Identifies a request and its associated presentation. All asynchronous callbacks must carry it.
/// </summary>
internal readonly record struct FloatingResultRequestIdentity(
    Guid SessionId,
    ContentType Mode,
    long RequestId,
    long PresentationId);

internal enum AnalysisFollowUpTurnStatus
{
    Loading,
    Completed,
    Failed,
    Cancelled
}

internal sealed record AnalysisFollowUpTurnState(
    int TurnNumber,
    string Question,
    string AnswerRawText,
    AnalysisFollowUpTurnStatus Status,
    long LastRequestId);

internal sealed record AnalysisConversationState(
    long? RootAnalysisRequestId,
    AnalysisSemanticSnapshot? SemanticSnapshot,
    string Draft,
    IReadOnlyList<AnalysisFollowUpTurnState> Turns)
{
    internal static AnalysisConversationState Empty() =>
        new(null, null, string.Empty, Array.Empty<AnalysisFollowUpTurnState>());
}

internal readonly record struct AnalysisFollowUpRequestIdentity(
    Guid SessionId,
    long RootAnalysisRequestId,
    int TurnNumber,
    long RequestId,
    long PresentationId);

/// <summary>
/// A read-only result session for one selected source text.
/// State transitions are owned exclusively by <see cref="FloatingResultSessionCoordinator"/>.
/// </summary>
internal sealed class FloatingResultSession
{
    private readonly Dictionary<ContentType, ModeResultState> _modeStates;
    private readonly ReadOnlyDictionary<ContentType, ModeResultState> _readOnlyModeStates;
    private AnalysisConversationState _analysisConversation = AnalysisConversationState.Empty();

    internal FloatingResultSession(
        Guid sessionId,
        string sourceText,
        FloatingWindowAnchor? anchor,
        ContentType activeMode,
        DetectionResult? detection = null)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
            throw new ArgumentException("Source text is required.", nameof(sourceText));

        SessionId = sessionId;
        SourceText = sourceText;
        Anchor = anchor;
        ActiveMode = activeMode;
        Detection = detection;
        _modeStates = Enum.GetValues<ContentType>()
            .ToDictionary(mode => mode, _ => ModeResultState.NotStarted());
        _readOnlyModeStates = new ReadOnlyDictionary<ContentType, ModeResultState>(_modeStates);
    }

    public Guid SessionId { get; }
    public string SourceText { get; }
    public FloatingWindowAnchor? Anchor { get; }
    public ContentType ActiveMode { get; private set; }
    public DetectionResult? Detection { get; }
    public IReadOnlyDictionary<ContentType, ModeResultState> ModeStates => _readOnlyModeStates;
    public AnalysisConversationState AnalysisConversation => _analysisConversation;

    internal ModeResultState GetModeState(ContentType mode) => _modeStates[mode];

    internal void SetActiveMode(ContentType mode) => ActiveMode = mode;

    internal void SetModeState(ContentType mode, ModeResultState state) => _modeStates[mode] = state;

    internal void SetAnalysisConversation(AnalysisConversationState state) => _analysisConversation = state;
}
