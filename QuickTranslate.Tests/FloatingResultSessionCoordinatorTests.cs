using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.UI;
using System.Windows;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class FloatingResultSessionCoordinatorTests
{
    [Fact]
    public void StartSession_CreatesIndependentModeStatesAndStartsInitialRequest()
    {
        var coordinator = new FloatingResultSessionCoordinator();

        var transition = coordinator.StartSession("source", ContentType.Code);

        var session = Assert.IsType<FloatingResultSession>(transition.Session);
        var identity = Assert.IsType<FloatingResultRequestIdentity>(transition.RequestIdentity);
        Assert.Equal(FloatingResultSessionTransitionKind.StartedRequest, transition.Kind);
        Assert.Equal(ContentType.Code, session.ActiveMode);
        Assert.Equal(4, session.ModeStates.Count);
        Assert.Equal(ModeResultStatus.Loading, session.ModeStates[ContentType.Code].Status);
        Assert.All(session.ModeStates.Where(pair => pair.Key != ContentType.Code), pair =>
            Assert.Equal(ModeResultStatus.NotStarted, pair.Value.Status));
        Assert.Equal(session.SessionId, identity.SessionId);
    }

    [Fact]
    public void StartSession_PreservesAnchorAndCompletedLookupReturnsOnlyCompletedStates()
    {
        var coordinator = new FloatingResultSessionCoordinator();
        var anchor = new FloatingWindowAnchor(new Point(10, 20), new Rect(1, 2, 3, 4));

        var transition = coordinator.StartSession("source", anchor, ContentType.Term);
        var identity = Assert.IsType<FloatingResultRequestIdentity>(transition.RequestIdentity);
        Assert.Equal(anchor, Assert.IsType<FloatingResultSession>(transition.Session).Anchor);
        Assert.False(coordinator.TryGetCompletedMode(ContentType.Term, out _));

        Assert.True(coordinator.TryComplete(identity, "definition"));
        Assert.True(coordinator.TryGetCompletedMode(ContentType.Term, out var completed));
        Assert.Equal("definition", completed!.RawText);
    }

    [Fact]
    public void SwitchMode_CancelsLoadingModeAndStartsNewRequest()
    {
        var coordinator = new FloatingResultSessionCoordinator();
        var first = coordinator.StartSession("source", ContentType.Translation);
        var firstIdentity = Assert.IsType<FloatingResultRequestIdentity>(first.RequestIdentity);

        var second = coordinator.SwitchMode(ContentType.Term);

        var session = Assert.IsType<FloatingResultSession>(second.Session);
        var secondIdentity = Assert.IsType<FloatingResultRequestIdentity>(second.RequestIdentity);
        Assert.Equal(ModeResultStatus.Cancelled, session.ModeStates[ContentType.Translation].Status);
        Assert.Equal(ModeResultStatus.Loading, session.ModeStates[ContentType.Term].Status);
        Assert.True(secondIdentity.RequestId > firstIdentity.RequestId);
        Assert.False(coordinator.TryComplete(firstIdentity, "stale"));
    }

    [Fact]
    public void SwitchMode_RestoresCompletedResultWithItsScrollState()
    {
        var coordinator = new FloatingResultSessionCoordinator();
        var initial = coordinator.StartSession("source", ContentType.Translation);
        var initialIdentity = Assert.IsType<FloatingResultRequestIdentity>(initial.RequestIdentity);
        Assert.True(coordinator.TryComplete(initialIdentity, "done"));
        var sessionId = Assert.IsType<FloatingResultSession>(initial.Session).SessionId;
        Assert.True(coordinator.TrySetScrollState(sessionId, ContentType.Translation, 42, false));

        var code = coordinator.SwitchMode(ContentType.Code);
        var codeIdentity = Assert.IsType<FloatingResultRequestIdentity>(code.RequestIdentity);
        Assert.True(coordinator.TryComplete(codeIdentity, "code result"));

        var restored = coordinator.SwitchMode(ContentType.Translation);
        var state = Assert.IsType<FloatingResultSession>(restored.Session).ModeStates[ContentType.Translation];
        Assert.Equal(FloatingResultSessionTransitionKind.RestoredCompleted, restored.Kind);
        Assert.Null(restored.RequestIdentity);
        Assert.Equal("done", state.RawText);
        Assert.Equal(42, state.ScrollOffset);
        Assert.False(state.AutoScrollEnabled);
    }

    [Fact]
    public void RefreshMode_ReplacesCompletedResultAndRejectsOldCallbacks()
    {
        var coordinator = new FloatingResultSessionCoordinator();
        var initial = coordinator.StartSession("source", ContentType.Analysis);
        var initialIdentity = Assert.IsType<FloatingResultRequestIdentity>(initial.RequestIdentity);
        Assert.True(coordinator.TryComplete(initialIdentity, "old result"));

        var refresh = coordinator.RefreshMode();
        var refreshIdentity = Assert.IsType<FloatingResultRequestIdentity>(refresh.RequestIdentity);
        var state = Assert.IsType<FloatingResultSession>(refresh.Session).ModeStates[ContentType.Analysis];
        Assert.Equal(ModeResultStatus.Loading, state.Status);
        Assert.Empty(state.RawText);
        Assert.False(coordinator.TryComplete(initialIdentity, "stale result"));
        Assert.True(coordinator.TryComplete(refreshIdentity, "new result"));
    }

    [Fact]
    public void NewSessionAndDismiss_RejectCallbacksFromEarlierSessions()
    {
        var coordinator = new FloatingResultSessionCoordinator();
        var first = coordinator.StartSession("first", ContentType.Term);
        var firstIdentity = Assert.IsType<FloatingResultRequestIdentity>(first.RequestIdentity);

        var second = coordinator.StartSession("second", ContentType.Term);
        Assert.False(coordinator.TryUpdateStreaming(firstIdentity, "stale"));
        var secondIdentity = Assert.IsType<FloatingResultRequestIdentity>(second.RequestIdentity);
        coordinator.DismissSession();
        Assert.False(coordinator.TryComplete(secondIdentity, "stale"));
        Assert.Null(coordinator.CurrentSession);
    }
}
