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

/// <summary>
/// A read-only result session for one selected source text.
/// State transitions are owned exclusively by <see cref="FloatingResultSessionCoordinator"/>.
/// </summary>
internal sealed class FloatingResultSession
{
    private readonly Dictionary<ContentType, ModeResultState> _modeStates;
    private readonly ReadOnlyDictionary<ContentType, ModeResultState> _readOnlyModeStates;

    internal FloatingResultSession(
        Guid sessionId,
        string sourceText,
        FloatingWindowAnchor? anchor,
        ContentType activeMode)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
            throw new ArgumentException("Source text is required.", nameof(sourceText));

        SessionId = sessionId;
        SourceText = sourceText;
        Anchor = anchor;
        ActiveMode = activeMode;
        _modeStates = Enum.GetValues<ContentType>()
            .ToDictionary(mode => mode, _ => ModeResultState.NotStarted());
        _readOnlyModeStates = new ReadOnlyDictionary<ContentType, ModeResultState>(_modeStates);
    }

    public Guid SessionId { get; }
    public string SourceText { get; }
    public FloatingWindowAnchor? Anchor { get; }
    public ContentType ActiveMode { get; private set; }
    public IReadOnlyDictionary<ContentType, ModeResultState> ModeStates => _readOnlyModeStates;

    internal ModeResultState GetModeState(ContentType mode) => _modeStates[mode];

    internal void SetActiveMode(ContentType mode) => ActiveMode = mode;

    internal void SetModeState(ContentType mode, ModeResultState state) => _modeStates[mode] = state;
}
