using QuickTranslate.Core;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class AnalysisConversationCoordinatorTests
{
    [Fact]
    public void CompletedAnalysis_StartsFollowUpAndCommitsCompletedExchange()
    {
        var coordinator = CompletedAnalysis();
        var session = coordinator.CurrentSession!;

        var followUp = coordinator.BeginFollowUp("  explain more  ");

        Assert.Equal(FloatingResultActiveOperationKind.FollowUp, coordinator.ActiveOperation?.Kind);
        Assert.Equal("explain more", followUp.Turn.Question);
        Assert.True(coordinator.TryUpdateFollowUpStreaming(followUp.RequestIdentity, "partial"));
        Assert.True(coordinator.TryCompleteFollowUp(followUp.RequestIdentity, "complete"));
        var turn = session.AnalysisConversation.Turns.Single();
        Assert.Equal(AnalysisFollowUpTurnStatus.Completed, turn.Status);
        Assert.Equal("complete", turn.AnswerRawText);
        var exchange = Assert.Single(coordinator.GetCompletedFollowUpExchanges(session.SessionId));
        Assert.Equal("explain more", exchange.Question);
        Assert.Equal("complete", exchange.Answer);
        Assert.Null(coordinator.ActiveOperation);
    }

    [Fact]
    public void NewQuestionMakesEarlierFailedTurnNonRetryable()
    {
        var coordinator = CompletedAnalysis();
        var first = coordinator.BeginFollowUp("q1");
        Assert.True(coordinator.TryFailFollowUp(first.RequestIdentity));

        var second = coordinator.BeginFollowUp("q2");
        Assert.Equal(2, second.Turn.TurnNumber);
        Assert.True(coordinator.TryFailFollowUp(second.RequestIdentity));

        var retry = coordinator.RetryLatestFollowUp();
        Assert.Equal(2, retry.Turn.TurnNumber);
        Assert.True(coordinator.TryCompleteFollowUp(retry.RequestIdentity, "a2"));
        Assert.Throws<InvalidOperationException>(() => coordinator.RetryLatestFollowUp());
        Assert.Equal(AnalysisFollowUpTurnStatus.Failed, coordinator.CurrentSession!.AnalysisConversation.Turns[0].Status);
    }

    [Fact]
    public void ModeSwitchCancelsFollowUpPreservesChainAndRejectsLateCallbacks()
    {
        var coordinator = CompletedAnalysis();
        var followUp = coordinator.BeginFollowUp("q1");

        var switched = coordinator.SwitchMode(ContentType.Translation);

        Assert.Equal(FloatingResultSessionTransitionKind.StartedRequest, switched.Kind);
        Assert.False(coordinator.TryCompleteFollowUp(followUp.RequestIdentity, "late"));
        Assert.Equal(
            AnalysisFollowUpTurnStatus.Cancelled,
            coordinator.CurrentSession!.AnalysisConversation.Turns.Single().Status);
        var translationIdentity = Assert.IsType<FloatingResultRequestIdentity>(switched.RequestIdentity);
        Assert.True(coordinator.TryComplete(translationIdentity, "translated"));
        var restored = coordinator.SwitchMode(ContentType.Analysis);
        Assert.Equal(FloatingResultSessionTransitionKind.RestoredCompleted, restored.Kind);
        Assert.Equal("q1", coordinator.CurrentSession.AnalysisConversation.Turns.Single().Question);
        Assert.Equal(1, coordinator.RetryLatestFollowUp().Turn.TurnNumber);
    }

    [Fact]
    public void HideCancellationOnlyCancelsFollowUpNotRootRequest()
    {
        var coordinator = new FloatingResultSessionCoordinator();
        var root = coordinator.StartSession("source", ContentType.Analysis);
        var rootIdentity = Assert.IsType<FloatingResultRequestIdentity>(root.RequestIdentity);

        Assert.False(coordinator.CancelActiveFollowUp());
        Assert.True(coordinator.TryComplete(
            rootIdentity,
            "root",
            new AnalysisSemanticSnapshot("prompt", "简体中文")));

        var followUp = coordinator.BeginFollowUp("q1");
        Assert.True(coordinator.CancelActiveFollowUp());
        Assert.False(coordinator.TryCompleteFollowUp(followUp.RequestIdentity, "late"));
        Assert.Equal(
            AnalysisFollowUpTurnStatus.Cancelled,
            coordinator.CurrentSession!.AnalysisConversation.Turns.Single().Status);
    }

    [Fact]
    public void RefreshAnalysisClearsConversationAndInvalidatesFollowUp()
    {
        var coordinator = CompletedAnalysis();
        var followUp = coordinator.BeginFollowUp("q1");

        var refresh = coordinator.RefreshMode();

        Assert.Empty(coordinator.CurrentSession!.AnalysisConversation.Turns);
        Assert.Null(coordinator.CurrentSession.AnalysisConversation.SemanticSnapshot);
        Assert.False(coordinator.TryUpdateFollowUpStreaming(followUp.RequestIdentity, "late"));
        Assert.Equal(FloatingResultActiveOperationKind.Root, coordinator.ActiveOperation?.Kind);
        Assert.NotNull(refresh.RequestIdentity);
    }

    [Fact]
    public void TenAcceptedQuestionsBlockQuestionElevenButAllowTailRetry()
    {
        var coordinator = CompletedAnalysis();
        AnalysisFollowUpTransition? last = null;
        for (var index = 1; index <= 10; index++)
        {
            last = coordinator.BeginFollowUp($"q{index}");
            Assert.True(coordinator.TryFailFollowUp(last.RequestIdentity));
        }

        Assert.Throws<InvalidOperationException>(() => coordinator.BeginFollowUp("q11"));
        var retry = coordinator.RetryLatestFollowUp();
        Assert.Equal(10, retry.Turn.TurnNumber);
    }

    [Fact]
    public void DraftIsSessionScopedAndClearedByNewSession()
    {
        var coordinator = CompletedAnalysis();
        var sessionId = coordinator.CurrentSession!.SessionId;
        Assert.True(coordinator.TrySetAnalysisDraft(sessionId, "draft"));
        Assert.Equal("draft", coordinator.CurrentSession.AnalysisConversation.Draft);

        coordinator.StartSession("new source", ContentType.Analysis);

        Assert.Empty(coordinator.CurrentSession!.AnalysisConversation.Draft);
        Assert.False(coordinator.TrySetAnalysisDraft(sessionId, "stale"));
    }

    private static FloatingResultSessionCoordinator CompletedAnalysis()
    {
        var coordinator = new FloatingResultSessionCoordinator();
        var transition = coordinator.StartSession("source", ContentType.Analysis);
        var identity = Assert.IsType<FloatingResultRequestIdentity>(transition.RequestIdentity);
        Assert.True(coordinator.TryComplete(
            identity,
            "root analysis",
            new AnalysisSemanticSnapshot("root prompt", "简体中文")));
        return coordinator;
    }
}
