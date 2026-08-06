using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
            Assert.True(window.TranslationTextBlock.IsReadOnly);
            Assert.True(window.TranslationTextBlock.Focusable);
            Assert.False(window.TranslationTextBlock.IsTabStop);
            Assert.True(window.MarkdownDocumentHost.IsReadOnly);
            Assert.True(window.MarkdownDocumentHost.Focusable);
            Assert.False(window.MarkdownDocumentHost.IsTabStop);
            window.MarkdownDocumentHost.SelectAll();
            Assert.True(ApplicationCommands.Copy.CanExecute(null, window.MarkdownDocumentHost));
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
                "why\nand how",
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
            Assert.True(window.ConversationRailColumn.Width.IsAuto);
            var turnPanel = Assert.IsType<StackPanel>(
                Assert.IsType<Border>(window.AnalysisTurnsPanel.Children[0]).Child);
            var questionHeader = Assert.IsType<Grid>(turnPanel.Children[0]);
            Assert.Collection(
                questionHeader.ColumnDefinitions,
                labelColumn => Assert.True(labelColumn.Width.IsAuto),
                questionColumn => Assert.Equal(GridUnitType.Star, questionColumn.Width.GridUnitType));
            var questionLabel = Assert.Single(questionHeader.Children.OfType<TextBlock>());
            var question = Assert.Single(questionHeader.Children.OfType<TextBox>());
            var answer = Assert.Single(turnPanel.Children.OfType<TextBox>());
            Assert.Equal("Q1", questionLabel.Text);
            Assert.Equal("why\nand how", question.Text);
            Assert.Equal(1, Grid.GetColumn(question));
            Assert.Equal(TextWrapping.Wrap, question.TextWrapping);
            AssertSelectable(question);
            AssertSelectable(answer);
            var streamingNode = window.GetConversationNodeForTests("Q1");
            var streamingBrush = Assert.IsType<SolidColorBrush>(streamingNode.Background);
            Assert.True(streamingBrush.HasAnimatedProperties);
            Assert.Equal(
                Color.FromRgb(0x25, 0x62, 0x5D),
                streamingBrush.GetAnimationBaseValue(SolidColorBrush.ColorProperty));

            var rootNode = window.GetConversationNodeForTests("解析");
            rootNode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("解析", window.CurrentConversationNodeKey);
            Assert.Equal(Color.FromRgb(0x44, 0x88, 0xFF), Assert.IsType<SolidColorBrush>(rootNode.Background).Color);
            Assert.True(Assert.IsType<SolidColorBrush>(streamingNode.Background).HasAnimatedProperties);

            var failed = loading with { Status = AnalysisFollowUpTurnStatus.Failed };
            window.SetSessionView(
                sessionId,
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([failed]));

            Assert.True(window.FollowUpTextBox.IsEnabled);
            var turnBorder = Assert.IsType<Border>(window.AnalysisTurnsPanel.Children[0]);
            var failedTurnPanel = Assert.IsType<StackPanel>(turnBorder.Child);
            var retry = Assert.Single(failedTurnPanel.Children.OfType<Button>());
            Assert.Equal("重试 Q1", AutomationProperties.GetName(retry));

            var failedNode = window.GetConversationNodeForTests("Q1");
            failedNode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Q1", window.CurrentConversationNodeKey);
            Assert.Equal(Color.FromRgb(0x44, 0x88, 0xFF), Assert.IsType<SolidColorBrush>(failedNode.Background).Color);
            Assert.Equal(Brushes.Transparent, window.GetConversationNodeForTests("解析").Background);
        });
    }

    [SkippableFact]
    public void Scrolling_SelectsTheSectionWithTheLargestVisibleArea()
    {
        RunOnSta(window =>
        {
            var root = string.Join("\n\n", Enumerable.Repeat("root analysis content", 80));
            var answer = string.Join("\n\n", Enumerable.Repeat("follow-up answer content", 80));
            var turn = new AnalysisFollowUpTurnState(
                1,
                "why",
                answer,
                AnalysisFollowUpTurnStatus.Completed,
                2);
            window.SizeToContent = SizeToContent.Manual;
            window.Height = 300;
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed(root),
                Conversation([turn]));
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();

            window.TranslationScroller.ScrollToHome();
            PumpDispatcher();
            Assert.Equal("解析", window.CurrentConversationNodeKey);

            window.TranslationScroller.ScrollToEnd();
            PumpDispatcher();
            Assert.Equal("Q1", window.CurrentConversationNodeKey);
        });
    }

    [SkippableFact]
    public void ClickingNode_KeepsSelectionUntilUserScrolls()
    {
        RunOnSta(window =>
        {
            var root = string.Join("\n\n", Enumerable.Repeat("root analysis content", 100));
            var turn = new AnalysisFollowUpTurnState(
                1,
                "why",
                "short follow-up answer",
                AnalysisFollowUpTurnStatus.Completed,
                2);
            window.SizeToContent = SizeToContent.Manual;
            window.Height = 300;
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed(root),
                Conversation([turn]));
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();
            window.TranslationScroller.ScrollToHome();
            PumpDispatcher();
            Assert.Equal("解析", window.CurrentConversationNodeKey);

            window.GetConversationNodeForTests("Q1")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher();

            Assert.Equal("Q1", window.CurrentConversationNodeKey);
            Assert.True(window.TranslationScroller.VerticalOffset > 0);

            RaisePreviewKeyDown(window.TranslationScroller, Key.Home);
            window.TranslationScroller.ScrollToHome();
            PumpDispatcher();
            Assert.Equal("解析", window.CurrentConversationNodeKey);
        });
    }

    [SkippableFact]
    public void ClickingCompletedNode_DuringLaterStreaming_KeepsNavigationPinned()
    {
        RunOnSta(window =>
        {
            var root = string.Join("\n\n", Enumerable.Repeat("root analysis content", 50));
            var completed = new AnalysisFollowUpTurnState(
                1,
                "first question",
                string.Join("\n\n", Enumerable.Repeat("completed answer", 40)),
                AnalysisFollowUpTurnStatus.Completed,
                2);
            var loading = new AnalysisFollowUpTurnState(
                2,
                "second question",
                string.Join("\n", Enumerable.Repeat("streaming answer", 40)),
                AnalysisFollowUpTurnStatus.Loading,
                3);
            var presentationId = window.BeginReplacement();
            window.SizeToContent = SizeToContent.Manual;
            window.Height = 300;
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed(root),
                Conversation([completed, loading]));
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();

            window.GetConversationNodeForTests("Q1")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher();
            var selectedOffset = window.TranslationScroller.VerticalOffset;

            Assert.Equal("Q1", window.CurrentConversationNodeKey);
            Assert.False(window.IsAutoScrollEnabledForTests);

            window.UpdateAnalysisFollowUpStreaming(
                presentationId,
                loading with
                {
                    AnswerRawText = string.Join("\n", Enumerable.Repeat("streaming answer growth", 100))
                });
            window.UpdateLayout();
            PumpDispatcher();

            Assert.Equal("Q1", window.CurrentConversationNodeKey);
            Assert.False(window.IsAutoScrollEnabledForTests);
            Assert.InRange(Math.Abs(window.TranslationScroller.VerticalOffset - selectedOffset), 0, 1);
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

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void RaisePreviewKeyDown(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target);
        Assert.NotNull(source);
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
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

    private static void AssertSelectable(TextBox textBox)
    {
        Assert.True(textBox.IsReadOnly);
        Assert.True(textBox.Focusable);
        Assert.False(textBox.IsTabStop);
        textBox.SelectAll();
        Assert.True(ApplicationCommands.Copy.CanExecute(null, textBox));
    }
}
