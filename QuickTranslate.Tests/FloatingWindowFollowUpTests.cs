using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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

    [Fact]
    public void StreamingUiThrottle_AllowsFirstAndIntervalBoundaryOnly()
    {
        var interval = TimeSpan.FromMilliseconds(100);
        var start = Stopwatch.Frequency;
        var beforeBoundary = start + (long)(Stopwatch.Frequency * 0.099);
        var boundary = start + (long)(Stopwatch.Frequency * 0.100);
        long lastTimestamp = 0;

        Assert.True(FloatingWindow.ShouldRunStreamingAction(ref lastTimestamp, start, interval));
        Assert.False(FloatingWindow.ShouldRunStreamingAction(ref lastTimestamp, beforeBoundary, interval));
        Assert.True(FloatingWindow.ShouldRunStreamingAction(ref lastTimestamp, boundary, interval));
    }

    [SkippableFact]
    public void RootThought_UsesMarkdownViewAndIsExcludedFromCopyText()
    {
        RunOnSta(window =>
        {
            var presentationId = window.BeginReplacement();
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed("final answer"),
                Conversation(turns: []));
            window.BeginRootThought(presentationId);

            var thought = Assert.IsType<ThoughtBlockView>(window.RootThoughtBlockForTests);
            Assert.Equal(Visibility.Collapsed, thought.Root.Visibility);
            Assert.True(thought.IsElapsedTimerEnabledForTests);

            window.UpdateRootThought(presentationId, "private reasoning", isTruncated: false);

            Assert.Equal(Visibility.Visible, thought.Root.Visibility);
            Assert.True(thought.IsExpandedForTests);
            Assert.StartsWith("正在思考… ", thought.StatusTextForTests.Text, StringComparison.Ordinal);
            Assert.Equal("收起思考", AutomationProperties.GetName(thought.ToggleButtonForTests));
            Assert.Equal("final answer", window.CopyTextForTests);
            Assert.True(thought.StableMarkdownHostForTests.IsReadOnly);
            Assert.True(thought.StableMarkdownHostForTests.Focusable);
            Assert.False(thought.StableMarkdownHostForTests.IsTabStop);
            Assert.True(thought.ShowsScrollbarHintForTests);
            thought.ActiveTextHostForTests.SelectAll();
            Assert.True(ApplicationCommands.Copy.CanExecute(null, thought.ActiveTextHostForTests));

            window.CompleteRootThought(presentationId, isTruncated: false);
            Assert.False(thought.IsElapsedTimerEnabledForTests);
        });
    }

    [SkippableFact]
    public void SecondarySelection_UsesOnlyFocusedAnswerHostAndExcludesThoughts()
    {
        RunOnSta(window =>
        {
            window.TranslationTextBlock.Text = "first selected answer";
            window.TranslationTextBlock.Select(6, 8);

            Assert.True(window.TryGetSecondarySelectionFromFocusedElement(
                window.TranslationTextBlock,
                out var selectedText));
            Assert.Equal("selected", selectedText);

            // A selection retained by another control must not be reused when
            // focus has moved elsewhere.
            Assert.False(window.TryGetSecondarySelectionFromFocusedElement(
                window.FollowUpTextBox,
                out _));

            var presentationId = window.BeginReplacement();
            window.BeginRootThought(presentationId);
            window.UpdateRootThought(presentationId, "private reasoning", isTruncated: false);
            var thought = Assert.IsType<ThoughtBlockView>(window.RootThoughtBlockForTests);
            thought.ActiveTextHostForTests.Text = "private reasoning";
            thought.ActiveTextHostForTests.SelectAll();

            Assert.False(window.TryGetSecondarySelectionFromFocusedElement(
                thought.ActiveTextHostForTests,
                out _));
        });
    }

    [Theory]
    [InlineData(SelectionGestureKind.MultiClick, 120, 120, 120, 120, true)]
    [InlineData(SelectionGestureKind.MultiClick, 500, 500, 500, 500, false)]
    [InlineData(SelectionGestureKind.Drag, 80, 120, 180, 120, true)]
    [InlineData(SelectionGestureKind.Drag, 500, 500, 650, 500, false)]
    public void SelectionGestureConsistency_RequiresGestureNearConfirmedSelection(
        SelectionGestureKind gesture,
        double startX,
        double startY,
        double endX,
        double endY,
        bool expected)
    {
        var location = new SelectionLocation
        {
            IsValid = true,
            Bounds = new Rect(100, 100, 100, 40)
        };
        var intent = new SelectionIntent(
            gesture,
            new Point(startX, startY),
            new Point(endX, endY),
            DateTimeOffset.UtcNow);

        Assert.Equal(expected, App.IsSelectionGestureConsistent(location, intent));
    }

    [Fact]
    public void SelectionProbePoints_PrioritizeReleasePointAndDeduplicateMultiClick()
    {
        var dragPoints = SelectionLocator.CreateGestureProbePoints(
            new Point(10, 20),
            new Point(80, 40));
        var clickPoints = SelectionLocator.CreateGestureProbePoints(
            new Point(30, 50),
            new Point(30, 50));

        Assert.Equal([new Point(80, 40), new Point(10, 20)], dragPoints);
        Assert.Equal([new Point(30, 50)], clickPoints);
    }

    [Theory]
    [InlineData(120, 120, true)]
    [InlineData(76, 120, true)]
    [InlineData(75, 120, false)]
    [InlineData(500, 500, false)]
    public void SelectionProbeMatch_RequiresConfirmedBoundsNearGesture(
        double x,
        double y,
        bool expected)
    {
        var location = new SelectionLocation
        {
            IsValid = true,
            Bounds = new Rect(100, 100, 100, 40)
        };

        Assert.Equal(
            expected,
            SelectionLocator.IsSelectionNearProbePoints(location, [new Point(x, y)]));
    }

    [SkippableFact]
    public void RootThought_PreservesPerModeStateAcrossModeSwitchButClearsOnRegeneration()
    {
        RunOnSta(window =>
        {
            var sessionId = Guid.NewGuid();
            var firstPresentation = window.BeginReplacement();
            window.SetSessionView(
                sessionId,
                ContentType.Translation,
                Completed("translation"),
                Conversation(turns: []));
            window.BeginRootThought(firstPresentation);
            window.UpdateRootThought(firstPresentation, "translation reasoning", isTruncated: false);
            window.CompleteRootThought(firstPresentation, isTruncated: false);

            var codePresentation = window.BeginReplacement(firstPresentation + 1, preserveThoughts: true);
            window.SetSessionView(
                sessionId,
                ContentType.Code,
                Completed("code"),
                Conversation(turns: []));
            window.BeginRootThought(codePresentation);
            window.UpdateRootThought(codePresentation, "code reasoning", isTruncated: false);
            window.CompleteRootThought(codePresentation, isTruncated: false);

            var restoredPresentation = window.BeginReplacement(codePresentation + 1, preserveThoughts: true);
            window.SetSessionView(
                sessionId,
                ContentType.Translation,
                Completed("translation"),
                Conversation(turns: []));
            Assert.Equal(Visibility.Visible, window.RootThoughtBlockForTests!.Root.Visibility);

            var regeneratedPresentation = window.BeginReplacement(
                restoredPresentation + 1,
                FloatingWindow.ThoughtResetScope.ClearActiveMode);
            window.SetSessionView(
                sessionId,
                ContentType.Translation,
                new ModeResultState(
                    ModeResultStatus.Loading,
                    string.Empty,
                    null,
                    3,
                    0,
                    true),
                Conversation(turns: []));
            window.BeginRootThought(regeneratedPresentation);
            Assert.Equal(Visibility.Collapsed, window.RootThoughtBlockForTests!.Root.Visibility);

            var codeAgainPresentation = window.BeginReplacement(
                regeneratedPresentation + 1,
                FloatingWindow.ThoughtResetScope.PreserveSession);
            window.SetSessionView(
                sessionId,
                ContentType.Code,
                Completed("code"),
                Conversation(turns: []));
            Assert.Equal(Visibility.Visible, window.RootThoughtBlockForTests!.Root.Visibility);
        });
    }

    [SkippableFact]
    public void RootThought_RejectsStalePresentationAndCollapsesOnCompletion()
    {
        RunOnSta(window =>
        {
            var stalePresentationId = window.BeginReplacement();
            var currentPresentationId = window.BeginReplacement();
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed("final answer"),
                Conversation(turns: []));
            window.BeginRootThought(currentPresentationId);

            window.UpdateRootThought(stalePresentationId, "stale", isTruncated: false);
            Assert.Equal(Visibility.Collapsed, window.RootThoughtBlockForTests!.Root.Visibility);

            window.UpdateRootThought(currentPresentationId, "bounded", isTruncated: true);
            window.CompleteRootThought(currentPresentationId, isTruncated: true);
            var thought = window.RootThoughtBlockForTests!;
            Assert.Equal(Visibility.Visible, thought.Root.Visibility);
            Assert.False(thought.IsExpandedForTests);
            Assert.StartsWith("思考了 ", thought.StatusTextForTests.Text, StringComparison.Ordinal);
            Assert.EndsWith("· 内容已截断", thought.StatusTextForTests.Text, StringComparison.Ordinal);
            Assert.Equal("展开思考", AutomationProperties.GetName(thought.ToggleButtonForTests));
        });
    }

    [SkippableFact]
    public void FollowUpThoughts_AreIndependentAndArrowTogglesVisibility()
    {
        RunOnSta(window =>
        {
            var sessionId = Guid.NewGuid();
            window.SetSessionView(
                sessionId,
                ContentType.Analysis,
                Completed("root"),
                Conversation([
                    new AnalysisFollowUpTurnState(1, "one", "answer one", AnalysisFollowUpTurnStatus.Completed, 1),
                    new AnalysisFollowUpTurnState(2, "two", "answer two", AnalysisFollowUpTurnStatus.Completed, 2)]));

            var first = window.BeginFollowUpThought(1);
            first.Update("first thought", false, ThoughtBlockStatus.Completed);
            var second = window.BeginFollowUpThought(2);
            second.Update("second thought", false, ThoughtBlockStatus.Completed);

            Assert.NotSame(first, second);
            Assert.Equal(Visibility.Visible, first.Root.Visibility);
            Assert.Equal(Visibility.Visible, second.Root.Visibility);
            Assert.False(first.IsExpandedForTests);
            Assert.False(second.IsExpandedForTests);

            second.ToggleButtonForTests.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(second.IsExpandedForTests);
            Assert.False(first.IsExpandedForTests);
        });
    }

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
            Assert.False(window.MarkdownDocumentHost.IsUndoEnabled);
            Assert.True(window.MarkdownDocumentHost.Focusable);
            Assert.False(window.MarkdownDocumentHost.IsTabStop);
            window.MarkdownDocumentHost.SelectAll();
            Assert.True(ApplicationCommands.Copy.CanExecute(null, window.MarkdownDocumentHost));
        });
    }

    [SkippableFact]
    public void RootStreamingMarkdown_UsesSeparateStableAndActiveHosts()
    {
        RunOnSta(window =>
        {
            var presentationId = window.BeginReplacement();

            window.UpdateTranslation(presentationId, "# Heading\n\nactive tail");

            Assert.Equal(Visibility.Collapsed, window.TranslationTextBlock.Visibility);
            Assert.Equal(Visibility.Collapsed, window.MarkdownDocumentHost.Visibility);
            Assert.Equal(Visibility.Visible, window.StreamingMarkdownHost.Visibility);
            Assert.Equal(Visibility.Visible, window.StreamingStableMarkdownHost.Visibility);
            Assert.Equal(Visibility.Visible, window.StreamingActiveTextHost.Visibility);
            Assert.Equal(Visibility.Collapsed, window.StreamingActiveMarkdownHost.Visibility);
            Assert.True(window.StreamingActiveTextHost.IsReadOnly);
            Assert.True(window.StreamingActiveTextHost.Focusable);
            Assert.False(window.StreamingActiveTextHost.IsTabStop);
            window.StreamingActiveTextHost.SelectAll();
            Assert.True(ApplicationCommands.Copy.CanExecute(null, window.StreamingActiveTextHost));
            Assert.False(window.StreamingStableMarkdownHost.IsUndoEnabled);
            Assert.False(window.StreamingActiveMarkdownHost.IsUndoEnabled);
            Assert.Equal(
                "Heading\r\n",
                new TextRange(
                    window.StreamingStableMarkdownHost.Document.ContentStart,
                    window.StreamingStableMarkdownHost.Document.ContentEnd).Text);
            Assert.Equal("active tail", window.StreamingActiveTextHost.Text);

            window.UpdateTranslation(presentationId, "# Heading\n\nactive tail **bold**");

            Assert.Equal(Visibility.Collapsed, window.StreamingActiveTextHost.Visibility);
            Assert.Equal(Visibility.Visible, window.StreamingActiveMarkdownHost.Visibility);
            Assert.Contains(
                Assert.IsType<Paragraph>(window.StreamingActiveMarkdownHost.Document.Blocks.FirstBlock).Inlines,
                inline => inline is Span span && span.FontWeight == FontWeights.Bold);

            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Translation,
                Completed("# Heading\n\ncompleted"));

            Assert.Equal(Visibility.Collapsed, window.StreamingMarkdownHost.Visibility);
            Assert.Equal(string.Empty, window.StreamingActiveTextHost.Text);
            Assert.Empty(window.StreamingStableMarkdownHost.Document.Blocks);
            Assert.Empty(window.StreamingActiveMarkdownHost.Document.Blocks);
            Assert.Equal(Visibility.Visible, window.MarkdownDocumentHost.Visibility);
        });
    }

    [SkippableFact]
    public void CompletedFollowUpMarkdown_DisablesUndoHistory()
    {
        RunOnSta(window =>
        {
            var turn = new AnalysisFollowUpTurnState(
                1,
                "why",
                "```csharp\nvar value = 1;\n```",
                AnalysisFollowUpTurnStatus.Completed,
                2);
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([turn]));

            var turnPanel = Assert.IsType<StackPanel>(
                Assert.IsType<Border>(window.AnalysisTurnsPanel.Children[0]).Child);
            var markdown = Assert.Single(turnPanel.Children.OfType<RichTextBox>());
            Assert.True(markdown.IsReadOnly);
            Assert.False(markdown.IsUndoEnabled);
        });
    }

    [SkippableFact]
    public void MarkdownSelection_FreezesOnlyTheSelectedResultScope()
    {
        RunOnSta(window =>
        {
            var presentationId = window.BeginReplacement();
            var loading = new AnalysisFollowUpTurnState(
                1,
                "why",
                string.Empty,
                AnalysisFollowUpTurnStatus.Loading,
                2);
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed("# Root analysis"),
                Conversation([loading]));
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();

            Assert.True(window.MarkdownDocumentHost.Focus());
            window.MarkdownDocumentHost.SelectAll();
            PumpDispatcher();

            window.UpdateAnalysisFollowUpStreaming(
                presentationId,
                loading with { AnswerRawText = "follow-up streamed while root is selected" });

            var turnPanel = Assert.IsType<StackPanel>(
                Assert.IsType<Border>(window.AnalysisTurnsPanel.Children[0]).Child);
            var followUpMarkdown = Assert.Single(turnPanel.Children.OfType<RichTextBox>());
            Assert.Contains(
                "follow-up streamed while root is selected",
                new TextRange(
                    followUpMarkdown.Document.ContentStart,
                    followUpMarkdown.Document.ContentEnd).Text);

            Assert.True(followUpMarkdown.Focus());
            followUpMarkdown.SelectAll();
            PumpDispatcher();

            window.UpdateTranslation(
                presentationId,
                "# Stable root\n\nroot streamed while follow-up is selected");

            Assert.Equal(Visibility.Visible, window.StreamingActiveTextHost.Visibility);
            Assert.Equal(
                "root streamed while follow-up is selected",
                window.StreamingActiveTextHost.Text);

            Assert.True(window.StreamingStableMarkdownHost.Focus());
            window.StreamingStableMarkdownHost.SelectAll();
            PumpDispatcher();

            window.UpdateTranslation(
                presentationId,
                "# Stable root\n\nroot streamed while follow-up is selected and keeps growing");

            Assert.Equal(
                "root streamed while follow-up is selected and keeps growing",
                window.StreamingActiveTextHost.Text);
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
            Assert.True(window.FollowUpSendButton.IsEnabled);
            Assert.Equal("停止生成", AutomationProperties.GetName(window.FollowUpSendButton));
            Assert.Equal(1, window.AnalysisTurnViewCount);
            Assert.Equal(2, window.ConversationNodeCount);
            Assert.True(window.ConversationRailColumn.Width.IsAuto);
            var turnPanel = Assert.IsType<StackPanel>(
                Assert.IsType<Border>(window.AnalysisTurnsPanel.Children[0]).Child);
            var questionHeader = Assert.IsType<Grid>(turnPanel.Children[0]);
            Assert.Collection(
                questionHeader.ColumnDefinitions,
                labelColumn => Assert.True(labelColumn.Width.IsAuto),
                questionColumn => Assert.Equal(GridUnitType.Star, questionColumn.Width.GridUnitType),
                editColumn => Assert.True(editColumn.Width.IsAuto));
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

    [SkippableFact]
    public void FollowUpInput_PreservesMultilineText()
    {
        RunOnSta(window =>
        {
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation(turns: []));
            const string multilineQuestion = "first line\r\nsecond line\nthird line";

            window.FollowUpTextBox.Text = multilineQuestion;

            Assert.True(window.FollowUpTextBox.AcceptsReturn);
            Assert.Equal(TextWrapping.Wrap, window.FollowUpTextBox.TextWrapping);
            Assert.Equal(ScrollBarVisibility.Auto, window.FollowUpTextBox.VerticalScrollBarVisibility);
            Assert.Equal(multilineQuestion, window.FollowUpTextBox.Text);
            var inputScrollBarStyle = Assert.IsType<Style>(window.FollowUpTextBox.Resources[typeof(ScrollBar)]);
            Assert.Same(window.FindResource("Win11VerticalScrollBar"), inputScrollBarStyle.BasedOn);
            var opacitySetter = Assert.Single(
                inputScrollBarStyle.Setters.OfType<Setter>(),
                setter => setter.Property == UIElement.OpacityProperty);
            Assert.Equal(1d, opacitySetter.Value);
        });
    }

    [SkippableFact]
    public void FooterStaysVisibleAndAutoScrollUsesFloatingAffordance()
    {
        RunOnSta(window =>
        {
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Translation,
                Completed("result") with { AutoScrollEnabled = false });

            Assert.Equal(Visibility.Visible, window.StatusMessageBar.Visibility);
            Assert.Equal("已完成", window.StatusMessageText.Text);
            Assert.Equal(TextTrimming.CharacterEllipsis, window.StatusMessageText.TextTrimming);
            Assert.Equal(Visibility.Collapsed, window.StatusElapsedText.Visibility);
            Assert.Equal(48d, window.StatusElapsedText.MinWidth);
            Assert.Equal(Visibility.Collapsed, window.StatusMessageActionButton.Visibility);
            Assert.Equal(Color.FromRgb(0x20, 0x21, 0x2B), ((SolidColorBrush)window.StatusMessageBar.Background).Color);
            Assert.Equal(
                FloatingStatusMessage.GetAccentColors(FloatingStatusKind.Success).Indicator,
                ((SolidColorBrush)window.StatusIndicator.Fill).Color);
            Assert.Equal(0d, window.ConversationContentPanel.Margin.Bottom);
            Assert.True(FloatingWindow.ShouldShowReturnToLatest(
                autoScrollEnabled: false,
                scrollableHeight: 1,
                currentBottomReserve: 0));
            Assert.False(FloatingWindow.ShouldShowReturnToLatest(
                autoScrollEnabled: true,
                scrollableHeight: 1,
                currentBottomReserve: 0));
            Assert.False(FloatingWindow.ShouldShowReturnToLatest(
                autoScrollEnabled: false,
                scrollableHeight: 0.5,
                currentBottomReserve: 0));
            Assert.False(FloatingWindow.ShouldShowReturnToLatest(
                autoScrollEnabled: false,
                scrollableHeight: 40,
                currentBottomReserve: 40));
            Assert.True(FloatingWindow.ShouldShowReturnToLatest(
                autoScrollEnabled: false,
                scrollableHeight: 41,
                currentBottomReserve: 40));
        });
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0.999, null)]
    [InlineData(1, "1 秒")]
    [InlineData(9.999, "9 秒")]
    [InlineData(65.4, "65 秒")]
    public void GenerationElapsed_UsesStableWholeSecondSuffix(
        double totalSeconds,
        string? expected)
    {
        Assert.Equal(
            expected,
            FloatingWindow.FormatGenerationElapsed(TimeSpan.FromSeconds(totalSeconds)));
    }

    [SkippableFact]
    public void ReturnToLatestReserve_IsRemovedWhenReplacementContentFitsViewport()
    {
        RunOnSta(window =>
        {
            window.SizeToContent = SizeToContent.Manual;
            window.Height = 220;
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Translation,
                Completed(string.Join("\n", Enumerable.Repeat("long result line", 80))) with
                {
                    AutoScrollEnabled = false
                });
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();

            Assert.Equal(Visibility.Visible, window.ReturnToLatestButton.Visibility);
            Assert.Equal(40d, window.ConversationContentPanel.Margin.Bottom);

            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Translation,
                Completed("short result") with { AutoScrollEnabled = false });
            window.UpdateLayout();
            PumpDispatcher();

            Assert.Equal(Visibility.Collapsed, window.ReturnToLatestButton.Visibility);
            Assert.Equal(0d, window.ConversationContentPanel.Margin.Bottom);
        });
    }

    [SkippableFact]
    public void ModelSelector_UsesCompactCurrentNameAndConstrainedPopup()
    {
        RunOnSta(window =>
        {
            var profile = new ModelProfile(
                "provider:qwen",
                string.Empty,
                "Qwen/Qwen3-8B",
                "硅基流动",
                "https://api.siliconflow.cn/v1",
                "key");

            window.SetModelProfiles([profile], profile, enabled: true);

            Assert.Equal(144d, window.ModelSelector.Width);
            Assert.Equal(144d, window.ModelSelector.MaxWidth);
            Assert.Equal("Qwen3-8B", window.ModelSelector.ModelNameText.Text);
            Assert.Equal(312d, window.ModelSelector.PopupContainer.Width);
            Assert.Equal(312d, window.ModelSelector.ProfileList.MaxHeight);

            window.Show();
            window.ModelSelector.SelectorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher();

            Assert.True(window.ModelSelector.SelectorPopup.IsOpen);
            Assert.Same(window.ModelSelector.SelectorButton, window.ModelSelector.SelectorPopup.PlacementTarget);
            Assert.Equal(PlacementMode.Top, window.ModelSelector.SelectorPopup.Placement);
            Assert.Equal(0d, window.ModelSelector.SelectorPopup.HorizontalOffset);
            Assert.Equal(66d, window.ModelSelector.PopupAnchor.Margin.Right);
            var modelListScrollBarStyle = Assert.IsType<Style>(
                window.ModelSelector.ProfileList.Resources[typeof(ScrollBar)]);
            var baseScrollBarStyle = Assert.IsType<Style>(window.ModelSelector.FindResource("Win11VerticalScrollBar"));
            Assert.Same(baseScrollBarStyle, modelListScrollBarStyle.BasedOn);
            Assert.Equal(
                4d,
                Assert.Single(
                    baseScrollBarStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == FrameworkElement.WidthProperty).Value);
            Assert.Equal(
                4d,
                Assert.Single(
                    baseScrollBarStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == FrameworkElement.MinWidthProperty).Value);
            Assert.Equal(
                1d,
                Assert.Single(
                    modelListScrollBarStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == UIElement.OpacityProperty).Value);
            Assert.Single(window.ModelSelector.ProfileList.Items);

            window.SetModelProfiles([], null, enabled: false);

            Assert.False(window.ModelSelector.SelectorPopup.IsOpen);
            Assert.Equal(Visibility.Collapsed, window.ModelSelector.Visibility);
        });
    }

    [SkippableFact]
    public void ModelSelector_FirstClickOnLastVisibleProfile_SelectsWithoutMouseDownScroll()
    {
        RunOnSta(window =>
        {
            var profiles = Enumerable.Range(1, 7)
                .Select(index => new ModelProfile(
                    $"profile:{index}",
                    string.Empty,
                    $"model-{index}",
                    "provider",
                    "https://example.com/v1",
                    "key"))
                .ToArray();
            string? selectedProfileId = null;
            var selectionCount = 0;
            window.ModelProfileSelected += profileId =>
            {
                selectedProfileId = profileId;
                selectionCount++;
            };
            window.SetModelProfiles(profiles, profiles[0], enabled: true);
            window.Show();
            window.ModelSelector.SelectorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher();

            var lastEntry = window.ModelSelector.ProfileList.Items[^1];
            window.ModelSelector.ProfileList.ScrollIntoView(lastEntry);
            window.ModelSelector.ProfileList.UpdateLayout();
            var lastItem = Assert.IsType<ListBoxItem>(
                window.ModelSelector.ProfileList.ItemContainerGenerator.ContainerFromItem(lastEntry));
            var selectedBeforeMouseDown = window.ModelSelector.ProfileList.SelectedItem;

            var mouseDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Source = lastItem
            };
            lastItem.RaiseEvent(mouseDown);

            Assert.True(mouseDown.Handled);
            Assert.Same(selectedBeforeMouseDown, window.ModelSelector.ProfileList.SelectedItem);
            Assert.True(window.ModelSelector.SelectorPopup.IsOpen);

            var mouseUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseUpEvent,
                Source = lastItem
            };
            lastItem.RaiseEvent(mouseUp);

            Assert.True(mouseUp.Handled);
            Assert.False(window.ModelSelector.SelectorPopup.IsOpen);
            Assert.Equal(profiles[^1].Id, selectedProfileId);
            Assert.Equal(1, selectionCount);
        });
    }

    [SkippableFact]
    public void CompletedFollowUp_RestoresFocusToInput()
    {
        RunOnSta(window =>
        {
            var sessionId = Guid.NewGuid();
            window.Show();
            window.SetSessionView(
                sessionId,
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation(turns: []));
            window.FollowUpTextBox.Text = "continue";
            window.FollowUpTextBox.Focus();
            window.FollowUpSendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var loading = new AnalysisFollowUpTurnState(
                1,
                "continue",
                string.Empty,
                AnalysisFollowUpTurnStatus.Loading,
                1);
            window.SetSessionView(
                sessionId,
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([loading]));

            window.SetSessionView(
                sessionId,
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([loading with
                {
                    AnswerRawText = "completed answer",
                    Status = AnalysisFollowUpTurnStatus.Completed
                }]));
            PumpDispatcher();

            Assert.True(window.FollowUpTextBox.IsKeyboardFocused);
        });
    }

    [SkippableFact]
    public void LoadingSendButton_StopsAndRestoresCurrentTurnForEditing()
    {
        RunOnSta(window =>
        {
            var loading = new AnalysisFollowUpTurnState(
                1,
                "original question",
                "partial",
                AnalysisFollowUpTurnStatus.Loading,
                2);
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([loading]));
            var stopCount = 0;
            window.AnalysisFollowUpStopRequested += () => stopCount++;

            window.FollowUpSendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(1, stopCount);
            Assert.Equal("停止生成", window.FollowUpSendButton.ToolTip);
        });
    }

    [SkippableFact]
    public void CancelledFollowUp_PreservesPartialAnswerAndKeepsCancellationInStatusBar()
    {
        RunOnSta(window =>
        {
            var cancelled = new AnalysisFollowUpTurnState(
                1,
                "question",
                "**partial** answer",
                AnalysisFollowUpTurnStatus.Cancelled,
                2);
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([cancelled]));

            var turnPanel = Assert.IsType<StackPanel>(
                Assert.IsType<Border>(window.AnalysisTurnsPanel.Children[0]).Child);
            var answer = Assert.Single(turnPanel.Children.OfType<RichTextBox>());
            Assert.DoesNotContain(
                "**partial**",
                new TextRange(answer.Document.ContentStart, answer.Document.ContentEnd).Text);
            Assert.Equal(
                "partial answer",
                new TextRange(answer.Document.ContentStart, answer.Document.ContentEnd).Text.Trim());
            Assert.Equal("追问已取消，可重试", window.StatusMessageText.Text);
            Assert.Single(turnPanel.Children.OfType<Button>());
        });
    }

    [SkippableFact]
    public void FollowUpInput_UpDownMoveCaretToBoundsForSingleLineText()
    {
        RunOnSta(window =>
        {
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([]));
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();
            window.FollowUpTextBox.Text = "question";
            window.FollowUpTextBox.Focus();

            window.FollowUpTextBox.CaretIndex = 3;
            RaisePreviewKeyDown(window.FollowUpTextBox, Key.Up);
            Assert.Equal(0, window.FollowUpTextBox.CaretIndex);

            window.FollowUpTextBox.CaretIndex = 3;
            RaisePreviewKeyDown(window.FollowUpTextBox, Key.Down);
            Assert.Equal(window.FollowUpTextBox.Text.Length, window.FollowUpTextBox.CaretIndex);
        });
    }

    [SkippableTheory]
    [InlineData("first line\nsecond line\nthird line")]
    [InlineData("first line\r\nsecond line\r\nthird line")]
    public void FollowUpInput_LeavesUpDownNavigationToMultilineTextBox(string text)
    {
        RunOnSta(window =>
        {
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([]));
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();
            window.FollowUpTextBox.Text = text;
            window.FollowUpTextBox.Focus();
            window.FollowUpTextBox.CaretIndex = text.IndexOf("second line", StringComparison.Ordinal) + 3;
            window.FollowUpTextBox.UpdateLayout();
            var source = PresentationSource.FromVisual(window.FollowUpTextBox);
            Assert.NotNull(source);

            foreach (var key in new[] { Key.Up, Key.Down })
            {
                window.FollowUpTextBox.CaretIndex = text.IndexOf("second line", StringComparison.Ordinal) + 3;
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                window.FollowUpTextBox.RaiseEvent(args);
                Assert.False(args.Handled);
            }
        });
    }

    [SkippableTheory]
    [InlineData("first line\nsecond line\nthird line")]
    [InlineData("first line\r\nsecond line\r\nthird line")]
    public void FollowUpInput_UpDownMoveCaretToTextBoundsAtMultilineEdges(string text)
    {
        RunOnSta(window =>
        {
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([]));
            window.Show();
            window.FollowUpTextBox.Text = text;
            window.FollowUpTextBox.Focus();
            window.UpdateLayout();
            PumpDispatcher();

            window.FollowUpTextBox.CaretIndex = 3;
            RaisePreviewKeyDown(window.FollowUpTextBox, Key.Up);
            Assert.Equal(0, window.FollowUpTextBox.CaretIndex);

            window.FollowUpTextBox.CaretIndex = text.LastIndexOf("third line", StringComparison.Ordinal) + 3;
            RaisePreviewKeyDown(window.FollowUpTextBox, Key.Down);
            Assert.Equal(text.Length, window.FollowUpTextBox.CaretIndex);
        });
    }

    [SkippableFact]
    public void CompletedQuestion_EditResendsSameTurn()
    {
        RunOnSta(window =>
        {
            var completed = new AnalysisFollowUpTurnState(
                1,
                "original question",
                "answer",
                AnalysisFollowUpTurnStatus.Completed,
                2);
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([completed]));
            var turnPanel = Assert.IsType<StackPanel>(
                Assert.IsType<Border>(window.AnalysisTurnsPanel.Children[0]).Child);
            var questionHeader = Assert.IsType<Grid>(turnPanel.Children[0]);
            var edit = Assert.Single(questionHeader.Children.OfType<Button>());
            var replacements = new List<(int TurnNumber, string Question)>();
            window.AnalysisFollowUpReplaceRequested +=
                (turnNumber, question) => replacements.Add((turnNumber, question));

            edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();
            Assert.Equal(window.FollowUpTextBox.Text.Length, window.FollowUpTextBox.CaretIndex);
            window.FollowUpTextBox.Text = "edited question";
            window.FollowUpSendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal([(1, "edited question")], replacements);
        });
    }

    [SkippableFact]
    public void FollowUpStatusBar_TracksTailTurnLifecycle()
    {
        RunOnSta(window =>
        {
            var sessionId = Guid.NewGuid();
            var loading = new AnalysisFollowUpTurnState(
                1,
                "continue",
                string.Empty,
                AnalysisFollowUpTurnStatus.Loading,
                1);

            window.SetSessionView(
                sessionId,
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([loading]));
            Assert.Equal("正在生成", window.StatusMessageText.Text);

            window.SetSessionView(
                sessionId,
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([loading with
                {
                    AnswerRawText = "completed answer",
                    Status = AnalysisFollowUpTurnStatus.Completed
                }]));
            Assert.Equal("已完成", window.StatusMessageText.Text);

            window.SetSessionView(
                sessionId,
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([loading with { Status = AnalysisFollowUpTurnStatus.Failed }]));
            Assert.Equal("追问失败，可重试", window.StatusMessageText.Text);

            window.SetSessionView(
                sessionId,
                ContentType.Analysis,
                Completed("root analysis"),
                Conversation([loading with { Status = AnalysisFollowUpTurnStatus.Cancelled }]));
            Assert.Equal("追问已取消，可重试", window.StatusMessageText.Text);
        });
    }

    [SkippableFact]
    public void CancelledTranslation_PreservesStreamingMarkdownPreview()
    {
        RunOnSta(window =>
        {
            var sessionId = Guid.NewGuid();
            var presentationId = window.BeginReplacement();
            var partial = "# Heading\n\npartial **bold**";
            window.SetSessionView(
                sessionId,
                ContentType.Translation,
                new ModeResultState(ModeResultStatus.Loading, string.Empty, null, 1, 0, true));
            window.UpdateTranslation(presentationId, partial);

            window.SetSessionView(
                sessionId,
                ContentType.Translation,
                new ModeResultState(ModeResultStatus.Cancelled, partial, null, 1, 0, true));

            Assert.Equal(Visibility.Visible, window.StreamingMarkdownHost.Visibility);
            Assert.Equal(Visibility.Collapsed, window.TranslationTextBlock.Visibility);
            Assert.Equal("已停止，可重试或换模型", window.StatusMessageText.Text);
            Assert.Equal(Color.FromRgb(0x20, 0x21, 0x2B), ((SolidColorBrush)window.StatusMessageBar.Background).Color);
            Assert.Equal(
                FloatingStatusMessage.GetAccentColors(FloatingStatusKind.Warning).Indicator,
                ((SolidColorBrush)window.StatusIndicator.Fill).Color);
        });
    }

    [SkippableFact]
    public void TranslationDirectionAction_RemainsAvailableAndRaisesToggleIntent()
    {
        RunOnSta(window =>
        {
            var toggleCount = 0;
            window.TranslationDirectionToggleRequested += () => toggleCount++;
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Translation,
                Completed("result"));
            window.SetTranslationDirectionState(
                "简体中文",
                "English",
                isManual: false,
                enabled: true);

            Assert.Equal(Visibility.Visible, window.StatusMessageActionButton.Visibility);
            Assert.Equal("译为 English", window.StatusMessageActionButton.Content);
            Assert.Equal(22d, window.StatusMessageActionButton.Height);
            Assert.Equal(new Thickness(6, 0, 6, 0), window.StatusMessageActionButton.Padding);
            Assert.Equal(VerticalAlignment.Center, window.StatusMessageActionButton.VerticalAlignment);
            Assert.Equal(
                "使用当前模型将本段翻译为 English",
                window.StatusMessageActionButton.ToolTip);
            Assert.Equal(
                "将当前文本翻译为 English",
                AutomationProperties.GetName(window.StatusMessageActionButton));

            window.ShowSelectionCaptureFeedback("临时提示");
            Assert.Equal("临时提示", window.StatusMessageText.Text);
            Assert.Equal(Visibility.Visible, window.StatusMessageActionButton.Visibility);
            window.StatusMessageActionButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent, window.StatusMessageActionButton));
            Assert.Equal(1, toggleCount);

            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Code,
                Completed("code result"));
            window.SetTranslationDirectionState(
                "简体中文",
                "English",
                isManual: false,
                enabled: true);
            Assert.Equal(Visibility.Collapsed, window.StatusMessageActionButton.Visibility);
        });
    }

    [SkippableFact]
    public void ManualTranslationDirection_UsesTargetedLoadingStatus()
    {
        RunOnSta(window =>
        {
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Translation,
                Completed(string.Empty) with
                {
                    Status = ModeResultStatus.Loading,
                    Quality = ModeResultQuality.Unassessed
                });
            window.SetTranslationDirectionState(
                "English",
                "简体中文",
                isManual: true,
                enabled: true);

            Assert.Equal("正在译为 English", window.StatusMessageText.Text);
            Assert.Equal("译为简体中文", window.StatusMessageActionButton.Content);
        });
    }

    [SkippableFact]
    public void EchoWarning_UsesExplicitStatusAndKeepsDirectionAction()
    {
        RunOnSta(window =>
        {
            window.SetSessionView(
                Guid.NewGuid(),
                ContentType.Translation,
                Completed("echo") with { Quality = ModeResultQuality.EchoWarning });
            window.SetTranslationDirectionState(
                "简体中文",
                "English",
                isManual: false,
                enabled: true);

            Assert.Equal("结果与原文高度一致", window.StatusMessageText.Text);
            Assert.Equal(
                FloatingStatusMessage.GetAccentColors(FloatingStatusKind.Warning).Indicator,
                ((SolidColorBrush)window.StatusIndicator.Fill).Color);
            Assert.Equal(Visibility.Visible, window.StatusMessageActionButton.Visibility);
        });
    }

    [SkippableFact]
    public void LoadingState_ChangesRefreshButtonToStopAndBack()
    {
        RunOnSta(window =>
        {
            window.SetLoading(true);

            Assert.True(window.IsGenerationStopVisibleForTests);
            Assert.Equal("停止生成", window.RefreshButton.ToolTip);

            window.SetLoading(false);

            Assert.False(window.IsGenerationStopVisibleForTests);
            Assert.Equal("重新生成", window.RefreshButton.ToolTip);
        });
    }

    [SkippableFact]
    public void AutoHideSuppression_IsScopedAndReferenceCounted()
    {
        RunOnSta(window =>
        {
            window.SuspendAutoHide();
            window.SuspendAutoHide();
            Assert.True(window.IsAutoHideSuppressedForTests);

            window.ResumeAutoHide();
            Assert.True(window.IsAutoHideSuppressedForTests);

            window.ResumeAutoHide();
            Assert.False(window.IsAutoHideSuppressedForTests);
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
