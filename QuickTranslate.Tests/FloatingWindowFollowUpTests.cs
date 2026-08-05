using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class FloatingWindowFollowUpTests
{
    private static bool IsRunningOnCI =>
        Environment.GetEnvironmentVariable("CI") == "true" ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

    [SkippableFact]
    public void AnalysisCompleted_ShowsAccessibleFollowUpControls()
    {
        RunOnSta(window =>
        {
            var sessionId = Guid.NewGuid();
            window.SetSessionView(
                sessionId,
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation(turns: []));

            Assert.Equal(Visibility.Visible, window.AnalysisFollowUpInput.Visibility);
            Assert.Equal("解析追问输入", AutomationProperties.GetName(window.FollowUpTextBox));
            Assert.Equal("发送追问", AutomationProperties.GetName(window.FollowUpSendButton));
            Assert.True(window.FollowUpSendButton.IsEnabled);
            Assert.Equal(0, window.AnalysisTurnViewCount);
        });
    }

    [SkippableFact]
    public void LoadingAndFailedTail_RenderBusyAndRetryStates()
    {
        RunOnSta(window =>
        {
            var sessionId = Guid.NewGuid();
            var loading = new AnalysisFollowUpTurnState(
                1,
                "why",
                "partial",
                AnalysisFollowUpTurnStatus.Loading,
                2);
            window.SetSessionView(
                sessionId,
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([loading]));

            Assert.False(window.FollowUpTextBox.IsEnabled);
            Assert.False(window.FollowUpSendButton.IsEnabled);
            Assert.Equal(1, window.AnalysisTurnViewCount);
            Assert.Equal(2, window.ConversationNodeCount);

            var failed = loading with { Status = AnalysisFollowUpTurnStatus.Failed };
            window.SetSessionView(
                sessionId,
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([failed]));

            Assert.True(window.FollowUpTextBox.IsEnabled);
            var turnBorder = Assert.IsType<Border>(window.AnalysisTurnsPanel.Children[0]);
            var turnPanel = Assert.IsType<StackPanel>(turnBorder.Child);
            var retry = Assert.Single(turnPanel.Children.OfType<Button>());
            Assert.Equal("重试 Q1", AutomationProperties.GetName(retry));
        });
    }

    [SkippableFact]
    public void SendButton_EmitsOneNormalizedQuestion()
    {
        RunOnSta(window =>
        {
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation(turns: []));
            var questions = new List<string>();
            window.AnalysisFollowUpRequested += questions.Add;
            window.FollowUpTextBox.Text = "  explain this  ";

            window.FollowUpSendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(["explain this"], questions);
        });
    }

    private static void RunOnSta(Action<FloatingWindow> assertion)
    {
        Skip.If(IsRunningOnCI, "WPF window tests require a real message pump, unavailable on headless CI.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            FloatingWindow? window = null;
            try
            {
                window = new FloatingWindow();
                assertion(window);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window?.Close();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
    }

    private static ModeResultState Completed(string text) => new(
        ModeResultStatus.Completed,
        text,
        null,
        1,
        0,
        true);

    private static AnalysisConversationState Conversation(IReadOnlyList<AnalysisFollowUpTurnState> turns) => new(
        1,
        new AnalysisSemanticSnapshot("system", "简体中文"),
        string.Empty,
        turns);
}
