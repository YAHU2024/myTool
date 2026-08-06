using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using QuickTranslate.Core;
using QuickTranslate.Helpers;
using QuickTranslate.Models;
using QuickTranslate.Services;

namespace QuickTranslate.UI;

/// <summary>
/// A reusable result window positioned beside the source selection.
/// Request ownership remains outside this view; the window only reports intent and view state.
/// </summary>
public partial class FloatingWindow : Window
{
    private const double PlacementGapDip = 12;
    private const double DefaultWindowMinHeight = 120;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly DispatcherTimer _scrollBarHideTimer;
    private readonly LatestPresentationCoordinator _presentations = new();
    private readonly AutoScrollController _autoScroll = new();
    private bool _isMouseInside;
    private bool _isLoading;
    private bool _isProgrammaticScroll;
    private bool _isMarkdownExpanded;
    private bool _isDragging;
    private Point _dragStartCursorPhysical;
    private Point _dragStartWindowPhysical;
    private bool _userMoved;
    private bool _userResized;
    private bool _isSystemSizing;
    private HwndSource? _hwndSource;
    private const int WmNchittest = 0x0084;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int ResizeBorderPhysical = 8;
    private string _rawText = string.Empty;
    private ModeResultStatus _modeStatus = ModeResultStatus.NotStarted;
    private TtsPlaybackCoordinator? _tts;
    private bool _ttsEnabled = true;
    private string _ttsVoice = string.Empty;
    private double _ttsRate = 1.0;
    private int _ttsMaxChars = 2000;
    private bool _isTtsBusy;
    private DateTime _lastSpeakClickUtc = DateTime.MinValue;
    private const string SpeakIcon = "\uE768";
    private const string StopIcon = "\uE71A";
    private DispatcherTimer? _statusMessageTimer;
    private StatusMessageEntry? _persistentStatus;
    private StatusMessageEntry? _transientStatus;
    private Action? _statusAction;
    private FloatingWindowAnchor _anchor;
    private bool _hasAnchor;
    private long _lastPositioningTicks;
    private const long MinPositioningIntervalTicks = 30 * TimeSpan.TicksPerMillisecond;
    private double _lastPositionedHeight;
    private bool _placeAbove;
    private Guid _sessionId;
    private ContentType _activeMode = ContentType.Translation;
    private AnalysisConversationState _analysisConversation = AnalysisConversationState.Empty();
    private readonly Dictionary<int, TextBlock> _streamingFollowUpAnswers = new();
    private bool _isImeComposing;
    private bool _suppressDraftEvent;
    private string _copyText = string.Empty;
    private string _speechText = string.Empty;
    private readonly List<ConversationNodeView> _conversationNodes = [];
    private string? _currentConversationNodeKey;

    private static readonly Color ConversationNodeActiveColor = Color.FromRgb(0x44, 0x88, 0xFF);
    private static readonly Color ConversationNodeStreamingColor = Color.FromRgb(0x4D, 0xB6, 0xAC);
    private static readonly Color ConversationNodeStreamingDimColor = Color.FromRgb(0x25, 0x62, 0x5D);

    public event Action<ContentType>? ModeRequested;
    public event Action? RefreshRequested;
    public event Action? HideRequested;
    public event Action<Guid, ContentType, double, bool>? ScrollStateChanged;
    public event Action<string>? AnalysisFollowUpRequested;
    public event Action? AnalysisFollowUpRetryRequested;
    public event Action<Guid, string>? AnalysisDraftChanged;

    internal bool IsTtsBusy => _isTtsBusy;
    internal int AnalysisTurnViewCount => AnalysisTurnsPanel.Children.Count;
    internal int ConversationNodeCount => ConversationNodeRail.Children.Count;
    internal string? CurrentConversationNodeKey => _currentConversationNodeKey;

    internal Button GetConversationNodeForTests(string key) =>
        _conversationNodes.Single(node => node.Key == key).Button;

    public bool IsPinned { get; private set; }

    public FloatingWindow()
    {
        InitializeComponent();
        SourceInitialized += FloatingWindow_SourceInitialized;
        MarkdownDocumentHost.AddHandler(Button.ClickEvent, new RoutedEventHandler(MarkdownCodeCopyButton_Click));
        MarkdownDocumentHost.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(MarkdownLink_RequestNavigate));
        TitleBar.PreviewMouseLeftButtonDown += TitleBar_PreviewMouseLeftButtonDown;
        TitleBar.PreviewMouseMove += TitleBar_PreviewMouseMove;
        TitleBar.PreviewMouseLeftButtonUp += TitleBar_PreviewMouseLeftButtonUp;
        TitleBar.LostMouseCapture += TitleBar_LostMouseCapture;
        TextCompositionManager.AddPreviewTextInputStartHandler(FollowUpTextBox, OnFollowUpTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(FollowUpTextBox, OnFollowUpTextInputUpdate);
        TextCompositionManager.AddPreviewTextInputHandler(FollowUpTextBox, OnFollowUpTextInputCompleted);
        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoHideTimer.Tick += (_, _) =>
        {
            if (CanAutoHide())
                Hide();
            _autoHideTimer.Stop();
        };
        FollowUpTextBox.GotKeyboardFocus += (_, _) => _autoHideTimer.Stop();
        FollowUpTextBox.LostKeyboardFocus += (_, _) => ResetAutoHideTimer();

        _scrollBarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _scrollBarHideTimer.Tick += (_, _) =>
        {
            _scrollBarHideTimer.Stop();
            TranslationScroller.Tag = false;
        };

        MouseEnter += (_, _) =>
        {
            _isMouseInside = true;
            ResetAutoHideTimer();
        };
        MouseLeave += (_, _) =>
        {
            _isMouseInside = false;
            ResetAutoHideTimer();
        };
        Deactivated += (_, _) =>
        {
            if (CanAutoHide())
                Hide();
        };
    }

    internal FloatingWindowAnchor CurrentAnchor => _anchor;

    internal void AttachTts(TtsPlaybackCoordinator tts)
    {
        if (_tts is not null)
            _tts.StateChanged -= OnTtsStateChanged;
        _tts = tts;
        _tts.StateChanged += OnTtsStateChanged;
        _isTtsBusy = _tts.IsBusy(TtsPlaybackOwner.FloatingResult);
        RefreshSpeakButton();
    }

    internal void ApplyTtsSettings(bool enabled, string? voice, double rate, int maxChars)
    {
        _ttsEnabled = enabled;
        _ttsVoice = voice?.Trim() ?? string.Empty;
        _ttsRate = rate;
        _ttsMaxChars = maxChars > 0 ? maxChars : 2000;
        RefreshSpeakButton();
    }

    private void OnTtsStateChanged(TtsPlaybackState state)
    {
        var busy = state.IsBusy && state.Owner == TtsPlaybackOwner.FloatingResult;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnTtsStateChanged(state));
            return;
        }

        var wasBusy = _isTtsBusy;
        _isTtsBusy = busy;
        RefreshSpeakButton();
        if (wasBusy && !busy)
            ResetAutoHideTimer();
        else if (!wasBusy && busy)
            _autoHideTimer.Stop();
    }

    internal bool ShowExistingResult()
    {
        if (!_hasAnchor || string.IsNullOrWhiteSpace(_rawText))
            return false;

        Show();
        UpdateLayout();
        Opacity = 1;
        IsHitTestVisible = true;
        ResetAutoHideTimer();
        return true;
    }

    public new void Hide()
    {
        _ = StopTtsAsync();
        EndDragging(resetAutoHideTimer: false);
        _scrollBarHideTimer.Stop();
        TranslationScroller.Tag = false;
        _userMoved = false;
        _userResized = false;
        _isSystemSizing = false;
        SizeToContent = SizeToContent.Height;
        ClearAllStatusMessages();
        MinHeight = DefaultWindowMinHeight;
        base.Hide();
    }

    public long BeginReplacement()
    {
        var presentationId = _presentations.Begin();
        ResetForReplacement();
        return presentationId;
    }

    public long BeginReplacement(long presentationId)
    {
        _presentations.Begin(presentationId);
        ResetForReplacement();
        return presentationId;
    }

    public bool IsPresentationCurrent(long presentationId) => _presentations.IsCurrent(presentationId);

    /// <summary>
    /// Applies the current mode state from the session coordinator, including its saved scroll state.
    /// This method does not start work or mutate coordinator state.
    /// </summary>
    internal void SetSessionView(
        Guid sessionId,
        ContentType mode,
        ModeResultState state,
        AnalysisConversationState? analysisConversation = null)
    {
        // Persist the currently visible mode before its view is replaced.
        RaiseScrollStateChanged();

        if (_sessionId != sessionId || _activeMode != mode)
            _ = StopTtsAsync();

        _sessionId = sessionId;
        _activeMode = mode;
        _modeStatus = state.Status;
        SetActiveModeButton(mode);
        _rawText = state.RawText;
        _analysisConversation = mode == ContentType.Analysis
            ? analysisConversation ?? AnalysisConversationState.Empty()
            : AnalysisConversationState.Empty();
        _isMarkdownExpanded = false;
        _autoScroll.BeginRequest();
        if (!state.AutoScrollEnabled)
            _autoScroll.PauseForUpwardNavigation();
        UpdateAutoScrollAffordance();

        if (state.Status == ModeResultStatus.Completed)
            ShowCompletedMarkdown();
        else
            ShowPlainText();
        SetLoading(state.Status == ModeResultStatus.Loading);
        RenderAnalysisConversation();
        RefreshSpeakButton();

        var expectedSessionId = sessionId;
        var expectedMode = mode;
        Dispatcher.BeginInvoke(() =>
        {
            if (_sessionId == expectedSessionId && _activeMode == expectedMode)
                RestoreScrollState(state.ScrollOffset, state.AutoScrollEnabled);
        }, DispatcherPriority.Loaded);
    }

    public void SetLoading(bool isLoading)
    {
        if (isLoading)
        {
            _modeStatus = ModeResultStatus.Loading;
            _ = StopTtsAsync();
        }

        _isLoading = isLoading;
        LoadingIndicator.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        if (isLoading)
            ((Storyboard)Resources["LoadingDotsStoryboard"]).Begin(this, true);
        else
            ((Storyboard)Resources["LoadingDotsStoryboard"]).Remove(this);
        RefreshSpeakButton();
        ResetAutoHideTimer();
    }

    public void ResetPin()
    {
        IsPinned = false;
        UpdatePinVisual();
        ResetAutoHideTimer();
    }

    private void HideLoadingIndicator()
    {
        LoadingIndicator.Visibility = Visibility.Collapsed;
        ((Storyboard)Resources["LoadingDotsStoryboard"]).Remove(this);
    }

    internal async Task<bool> ShowTranslationAsync(
        long presentationId,
        string translation,
        FloatingWindowAnchor anchor,
        ContentType contentType,
        string? analysisSourceText = null)
    {
        if (!IsPresentationCurrent(presentationId))
            return false;

        _rawText = translation;
        _copyText = translation;
        _speechText = translation;
        if (!_isLoading)
            _modeStatus = ModeResultStatus.Completed;
        else
            _modeStatus = ModeResultStatus.Loading;
        ShowPlainText();
        _autoScroll.BeginRequest();
        UpdateAutoScrollAffordance();
        _anchor = anchor;
        _hasAnchor = true;
        SetActiveModeButton(contentType);

        var workArea = Win32Api.GetPhysicalWorkAreaAtPoint(anchor.PreferredPoint);
        var scale = DpiHelper.GetScaleForPhysicalPoint(anchor.PreferredPoint);
        var exclusionBounds = anchor.GetEffectiveExclusionBounds(scale);
        var chromeHeightDip = contentType == ContentType.Analysis ? 94 : 54;
        var gap = PlacementGapDip * scale.Y;
        var minimumWindowHeight = (80 + chromeHeightDip) * scale.Y;
        _placeAbove = FloatingWindowPlacement.ShouldPlaceAbove(exclusionBounds, workArea, minimumWindowHeight, gap);
        var availableHeight = _placeAbove
            ? exclusionBounds.Top - gap - workArea.Top
            : workArea.Bottom - exclusionBounds.Bottom - gap;
        TranslationScroller.MaxHeight = _userResized
            ? double.PositiveInfinity
            : Math.Max(availableHeight / scale.Y - chromeHeightDip, 80);

        Opacity = 0;
        IsHitTestVisible = false;
        Show();
        UpdateLayout();
        PositionWindowAtAnchor();

        await WaitForCompositionFrameAsync();
        if (!IsPresentationCurrent(presentationId))
            return false;

        UpdateLayout();
        PositionWindowAtAnchor();
        Opacity = 1;
        IsHitTestVisible = true;
        RefreshSpeakButton();
        ResetAutoHideTimer();
        return true;
    }

    public void UpdateTranslation(long presentationId, string translation)
    {
        if (!IsPresentationCurrent(presentationId))
            return;

        _rawText = translation;
        if (_analysisConversation.Turns.Count == 0)
        {
            _copyText = translation;
            _speechText = translation;
        }
        ShowPlainText(ensureFooter: false);
        if (_isLoading && !string.IsNullOrEmpty(translation))
            HideLoadingIndicator();
        if (_autoScroll.OnContentOrViewportChanged())
            ScrollToEndProgrammatically();

        // Throttle expensive work to ~30 fps so the dispatcher
        // has time to render between streaming chunks.
        var now = DateTime.UtcNow.Ticks;
        if (now - _lastPositioningTicks < MinPositioningIntervalTicks)
            return;
        _lastPositioningTicks = now;

        if (Math.Abs(ActualHeight - _lastPositionedHeight) > 0.5)
        {
            _lastPositionedHeight = ActualHeight;
            PositionWindowAtAnchor();
        }
        ResetAutoHideTimer();
    }

    internal void UpdateAnalysisFollowUpStreaming(
        long presentationId,
        AnalysisFollowUpTurnState turn)
    {
        if (!IsPresentationCurrent(presentationId) ||
            _sessionId == Guid.Empty ||
            _activeMode != ContentType.Analysis ||
            !_streamingFollowUpAnswers.TryGetValue(turn.TurnNumber, out var answer))
        {
            return;
        }

        answer.Text = string.IsNullOrEmpty(turn.AnswerRawText)
            ? "回答中..."
            : turn.AnswerRawText;
        if (_autoScroll.OnContentOrViewportChanged())
            ScrollToEndProgrammatically();

        var now = DateTime.UtcNow.Ticks;
        if (now - _lastPositioningTicks >= MinPositioningIntervalTicks)
        {
            _lastPositioningTicks = now;
            UpdateLayout();
            PositionWindowAtAnchor();
            UpdateCurrentConversationNodeFromViewport();
        }
    }

    /// <summary>
    /// Forces one final position update after streaming completes.
    /// Call this before rendering the completed markdown view so the
    /// last throttled frame is never lost.
    /// </summary>
    public void FlushStreamingUpdate()
    {
        if (string.IsNullOrWhiteSpace(_rawText))
            return;

        UpdateLayout();
        _lastPositionedHeight = ActualHeight;
        PositionWindowAtAnchor();
    }

    private void ShowPlainText(bool ensureFooter = true)
    {
        MarkdownDocumentHost.Visibility = Visibility.Collapsed;
        ExpandMarkdownButton.Visibility = Visibility.Collapsed;
        if (ensureFooter)
            EnsureFooterFitsWindow();
        TranslationTextBlock.Visibility = Visibility.Visible;
        TranslationTextBlock.Text = _rawText;
    }

    private void ShowCompletedMarkdown()
    {
        var maxDisplayCharacters = _isMarkdownExpanded ? int.MaxValue : MarkdownRenderer.DefaultMaxDisplayCharacters;
        if (!MarkdownRenderer.TryRender(_rawText, out var result, maxDisplayCharacters) || result.UsedPlainTextFallback)
        {
            if (result.Error is not null)
            {
                // Never include the selected/result text in logs.
                Logger.Error("FloatingWindow", "Markdown rendering failed; using the plain-text result view.", result.Error);
            }

            ShowPlainText();
            return;
        }

        MarkdownDocumentHost.Document = result.Document;
        TranslationTextBlock.Visibility = Visibility.Collapsed;
        MarkdownDocumentHost.Visibility = Visibility.Visible;
        ExpandMarkdownButton.Visibility = result.IsCollapsed ? Visibility.Visible : Visibility.Collapsed;
        EnsureFooterFitsWindow();
        UpdateLayout();
        PositionWindowAtAnchor();
        if (_autoScroll.IsAutoScrollEnabled)
            ScrollToEndProgrammatically();
    }

    private void RenderAnalysisConversation()
    {
        StopConversationNodeAnimations();
        _streamingFollowUpAnswers.Clear();
        AnalysisTurnsPanel.Children.Clear();
        ConversationNodeRail.Children.Clear();
        _conversationNodes.Clear();

        var turns = _analysisConversation.Turns;
        var canFollowUp = _activeMode == ContentType.Analysis &&
            _modeStatus == ModeResultStatus.Completed &&
            _analysisConversation.SemanticSnapshot is not null &&
            !string.IsNullOrWhiteSpace(_rawText);
        var hasTurns = canFollowUp && turns.Count > 0;
        AnalysisRootLabel.Visibility = hasTurns ? Visibility.Visible : Visibility.Collapsed;
        AnalysisTurnsPanel.Visibility = hasTurns ? Visibility.Visible : Visibility.Collapsed;
        ConversationNodeRail.Visibility = hasTurns ? Visibility.Visible : Visibility.Collapsed;
        ConversationRailColumn.Width = hasTurns ? GridLength.Auto : new GridLength(0);
        AnalysisFollowUpInput.Visibility = canFollowUp ? Visibility.Visible : Visibility.Collapsed;

        if (!canFollowUp)
        {
            _currentConversationNodeKey = null;
            _copyText = _rawText;
            _speechText = _rawText;
            return;
        }

        if (_currentConversationNodeKey != "解析" &&
            !turns.Any(turn => _currentConversationNodeKey == $"Q{turn.TurnNumber}"))
        {
            _currentConversationNodeKey = "解析";
        }

        AddConversationNode(
            "解析",
            "初始解析",
            RootResultHost,
            AnalysisRootLabel,
            isStreaming: false,
            isWarning: false);
        foreach (var turn in turns)
            AddFollowUpTurn(turn, turn == turns[^1]);

        var busy = turns.LastOrDefault()?.Status == AnalysisFollowUpTurnStatus.Loading;
        var limitReached = turns.Count >= 10;
        if (busy)
            _ = StopTtsAsync();
        FollowUpTextBox.IsEnabled = !busy && !limitReached;
        FollowUpSendButton.IsEnabled = !busy && !limitReached;
        FollowUpSendButton.Opacity = FollowUpSendButton.IsEnabled ? 1.0 : 0.45;
        FollowUpInputHint.Text = limitReached
            ? "已达到本次解析的 10 轮追问上限"
            : "继续追问...";
        FollowUpInputHint.Visibility = string.IsNullOrEmpty(FollowUpTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        _suppressDraftEvent = true;
        try
        {
            if (FollowUpTextBox.Text != _analysisConversation.Draft)
                FollowUpTextBox.Text = _analysisConversation.Draft;
        }
        finally
        {
            _suppressDraftEvent = false;
        }

        _copyText = turns.Count == 0
            ? _rawText
            : AnalysisConversationFormatter.BuildCopyText(_rawText, turns);
        _speechText = turns.LastOrDefault(turn => turn.Status == AnalysisFollowUpTurnStatus.Completed)?.AnswerRawText
            ?? _rawText;
        EnsureFooterFitsWindow();
        Dispatcher.BeginInvoke(UpdateCurrentConversationNodeFromViewport, DispatcherPriority.Loaded);
    }

    private void AddFollowUpTurn(AnalysisFollowUpTurnState turn, bool isTail)
    {
        var container = new StackPanel();
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x52)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(0, 10, 4, 0),
            Child = container
        };
        AnalysisTurnsPanel.Children.Add(border);

        var label = new TextBlock
        {
            Text = $"Q{turn.TurnNumber}",
            Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xC5, 0xFF)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Focusable = true
        };
        AutomationProperties.SetName(label, $"追问 Q{turn.TurnNumber}");
        container.Children.Add(label);
        container.Children.Add(new TextBlock
        {
            Text = turn.Question,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 6)
        });

        if (turn.Status == AnalysisFollowUpTurnStatus.Completed &&
            MarkdownRenderer.TryRender(turn.AnswerRawText, out var rendered, int.MaxValue) &&
            !rendered.UsedPlainTextFallback)
        {
            var markdown = new RichTextBox
            {
                Document = rendered.Document,
                IsReadOnly = true,
                IsDocumentEnabled = true,
                Focusable = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            markdown.AddHandler(Button.ClickEvent, new RoutedEventHandler(MarkdownCodeCopyButton_Click));
            markdown.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(MarkdownLink_RequestNavigate));
            container.Children.Add(markdown);
        }
        else
        {
            var answer = new TextBlock
            {
                Text = FollowUpStatusText(turn),
                Foreground = turn.Status is AnalysisFollowUpTurnStatus.Failed or AnalysisFollowUpTurnStatus.Cancelled
                    ? new SolidColorBrush(Color.FromRgb(0xD8, 0xB4, 0x7A))
                    : new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xEA)),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            };
            container.Children.Add(answer);
            if (turn.Status == AnalysisFollowUpTurnStatus.Loading)
                _streamingFollowUpAnswers[turn.TurnNumber] = answer;
        }

        if (isTail && turn.Status is AnalysisFollowUpTurnStatus.Failed or AnalysisFollowUpTurnStatus.Cancelled)
        {
            var retry = new Button
            {
                Content = "\uE72C",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                ToolTip = "重试本轮",
                Style = (Style)FindResource("IconToolbarButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0)
            };
            AutomationProperties.SetName(retry, $"重试 Q{turn.TurnNumber}");
            retry.Click += (_, _) => AnalysisFollowUpRetryRequested?.Invoke();
            container.Children.Add(retry);
        }

        AddConversationNode(
            $"Q{turn.TurnNumber}",
            AnalysisConversationFormatter.SummarizeQuestion(turn.Question),
            border,
            label,
            isStreaming: turn.Status == AnalysisFollowUpTurnStatus.Loading,
            isWarning: turn.Status is AnalysisFollowUpTurnStatus.Failed or AnalysisFollowUpTurnStatus.Cancelled);
    }

    private void AddConversationNode(
        string key,
        string toolTip,
        FrameworkElement target,
        FrameworkElement focusTarget,
        bool isStreaming,
        bool isWarning)
    {
        var button = new Button
        {
            Content = key,
            ToolTip = toolTip,
            Style = (Style)FindResource("ConversationNodeButton"),
            Background = Brushes.Transparent,
            BorderBrush = isWarning
                ? new SolidColorBrush(Color.FromRgb(0xD8, 0xB4, 0x7A))
                : new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x86)),
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetName(button, key == "解析" ? "定位到初始解析" : $"定位到{key}");
        button.Click += ConversationNode_Click;
        ConversationNodeRail.Children.Add(button);
        var node = new ConversationNodeView(key, button, target, focusTarget, isStreaming);
        button.Tag = node;
        focusTarget.GotKeyboardFocus += ConversationContent_GotKeyboardFocus;
        _conversationNodes.Add(node);
        ApplyConversationNodeVisual(node);
    }

    private static string FollowUpStatusText(AnalysisFollowUpTurnState turn) => turn.Status switch
    {
        AnalysisFollowUpTurnStatus.Loading => string.IsNullOrEmpty(turn.AnswerRawText) ? "回答中..." : turn.AnswerRawText,
        AnalysisFollowUpTurnStatus.Failed => "追问失败，请重试本轮。",
        AnalysisFollowUpTurnStatus.Cancelled => "追问已取消。",
        _ => turn.AnswerRawText
    };

    private void ConversationNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConversationNodeView node })
            return;
        SetCurrentConversationNode(node.Key);
        _isProgrammaticScroll = true;
        try
        {
            node.Target.BringIntoView(new Rect(new Size(
                Math.Max(1, node.Target.ActualWidth),
                Math.Max(1, node.Target.ActualHeight))));
            node.FocusTarget.Focus();
        }
        finally
        {
            _isProgrammaticScroll = false;
        }
        Dispatcher.BeginInvoke(UpdateCurrentConversationNodeFromViewport, DispatcherPriority.Loaded);
        RaiseScrollStateChanged();
    }

    private void ConversationContent_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var node = _conversationNodes.FirstOrDefault(candidate => candidate.FocusTarget == sender);
        if (node is not null)
            SetCurrentConversationNode(node.Key);
    }

    private void UpdateCurrentConversationNodeFromViewport()
    {
        if (_conversationNodes.Count == 0 ||
            TranslationScroller.ViewportHeight <= 0 ||
            !TranslationScroller.IsVisible)
        {
            return;
        }

        ConversationNodeView? current = null;
        var largestVisibleHeight = 0.0;
        foreach (var node in _conversationNodes)
        {
            if (!node.Target.IsVisible || node.Target.ActualHeight <= 0)
                continue;

            try
            {
                var top = node.Target.TranslatePoint(new Point(0, 0), TranslationScroller).Y;
                var bottom = top + node.Target.ActualHeight;
                var visibleHeight = Math.Max(
                    0,
                    Math.Min(bottom, TranslationScroller.ViewportHeight) - Math.Max(top, 0));
                if (visibleHeight > largestVisibleHeight)
                {
                    largestVisibleHeight = visibleHeight;
                    current = node;
                }
            }
            catch (InvalidOperationException)
            {
                // The visual tree may be rebuilding during a mode/session replacement.
            }
        }

        if (current is not null)
            SetCurrentConversationNode(current.Key);
    }

    private void SetCurrentConversationNode(string key)
    {
        if (_currentConversationNodeKey == key)
            return;
        _currentConversationNodeKey = key;
        foreach (var node in _conversationNodes)
            ApplyConversationNodeVisual(node);
    }

    private void ApplyConversationNodeVisual(ConversationNodeView node)
    {
        if (node.IsStreaming)
        {
            if (node.Button.Background is not SolidColorBrush brush ||
                brush.IsFrozen ||
                brush.Color != ConversationNodeStreamingDimColor)
            {
                brush = new SolidColorBrush(ConversationNodeStreamingDimColor);
                node.Button.Background = brush;
            }

            var animation = new ColorAnimation
            {
                From = ConversationNodeStreamingDimColor,
                To = ConversationNodeStreamingColor,
                Duration = TimeSpan.FromMilliseconds(900),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
            return;
        }

        if (node.Button.Background is SolidColorBrush { IsFrozen: false } animatedBrush)
            animatedBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        node.Button.Background = node.Key == _currentConversationNodeKey
            ? new SolidColorBrush(ConversationNodeActiveColor)
            : Brushes.Transparent;
    }

    private void StopConversationNodeAnimations()
    {
        foreach (var node in _conversationNodes)
        {
            if (node.Button.Background is SolidColorBrush { IsFrozen: false } brush)
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        }
    }

    private void MarkdownCodeCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Tag: MarkdownCodeBlock metadata } button)
            return;
        try
        {
            Clipboard.SetText(metadata.Code);
            TransientButtonFeedback.ShowCopySuccess(button, "\u29C9");
            e.Handled = true;
        }
        catch
        {
            // Clipboard access can be temporarily unavailable.
        }
    }

    private void ExpandMarkdownButton_Click(object sender, RoutedEventArgs e)
    {
        _isMarkdownExpanded = true;
        ShowCompletedMarkdown();
    }

    private void MarkdownLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (!MarkdownRenderer.IsSafeLink(e.Uri?.AbsoluteUri, out var uri) || uri is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Logger.Warn("FloatingWindow", $"Could not open a Markdown link: {exception.GetType().Name}");
        }
    }

    private void ResetForReplacement()
    {
        _ = StopTtsAsync();
        _autoHideTimer.Stop();
        _isMouseInside = false;
        _sessionId = Guid.Empty;
        _activeMode = ContentType.Translation;
        _modeStatus = ModeResultStatus.NotStarted;
        _autoScroll.BeginRequest();
        SetLoading(false);
        UpdateAutoScrollAffordance();
        Opacity = 0;
        IsHitTestVisible = false;
        Hide();
        _rawText = string.Empty;
        _copyText = string.Empty;
        _speechText = string.Empty;
        _analysisConversation = AnalysisConversationState.Empty();
        RenderAnalysisConversation();
        _isMarkdownExpanded = false;
        _lastPositioningTicks = 0;
        _lastPositionedHeight = 0;
        ShowPlainText();
        SetActiveModeButton(ContentType.Translation);
        RefreshSpeakButton();
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender == TranslationModeButton) RequestMode(ContentType.Translation);
        else if (sender == CodeModeButton) RequestMode(ContentType.Code);
        else if (sender == TermModeButton) RequestMode(ContentType.Term);
        else if (sender == AnalysisModeButton) RequestMode(ContentType.Analysis);
    }

    private void RequestMode(ContentType mode)
    {
        ModeRequested?.Invoke(mode);
        ResetAutoHideTimer();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke();
        ResetAutoHideTimer();
    }

    private void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        var text = string.IsNullOrWhiteSpace(_copyText) ? _rawText : _copyText;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            if (sender is Button btn)
                TransientButtonFeedback.ShowCopySuccess(btn, "\u29C9");
        }
        catch { /* Clipboard access can be temporarily unavailable. */ }
        ResetAutoHideTimer();
    }



    private async void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        ResetAutoHideTimer();
        if (_tts is null)
            return;

        if (_tts.IsBusy(TtsPlaybackOwner.FloatingResult) || _isTtsBusy)
        {
            await StopTtsAsync().ConfigureAwait(true);
            return;
        }

        // Debounce idle re-clicks so latest-wins churn is less likely.
        var now = DateTime.UtcNow;
        if ((now - _lastSpeakClickUtc).TotalMilliseconds < 300)
            return;
        _lastSpeakClickUtc = now;

        var sourceText = string.IsNullOrWhiteSpace(_speechText) ? _rawText : _speechText;
        if (!TtsTextSelector.CanSpeak(_modeStatus, sourceText, _ttsEnabled))
            return;

        var speechText = TtsTextSelector.NormalizeForSpeech(sourceText, _ttsMaxChars, out var truncated);
        if (string.IsNullOrWhiteSpace(speechText))
            return;

        if (truncated)
        {
            Logger.Warn("FloatingWindow", "tts.speak.truncated", new Dictionary<string, object?>
            {
                ["text_len"] = sourceText.Length,
                ["max_chars"] = _ttsMaxChars
            });
            ShowTransientStatus("文本过长，已截断朗读", FloatingStatusKind.Warning);
        }

        string? successHint = null;
        try
        {
            var voiceOverride = string.IsNullOrWhiteSpace(_ttsVoice) ? null : _ttsVoice;
            await _tts.SpeakAsync(
                TtsPlaybackOwner.FloatingResult,
                speechText,
                languageHint: null,
                voiceOverride,
                _ttsRate,
                CancellationToken.None).ConfigureAwait(true);

            successHint = _tts.TakeLastUiHint();
        }
        catch (OperationCanceledException)
        {
            // Stopped by lifecycle or user.
        }
        catch (TtsSpeakException ex)
        {
            ShowSpeakFailureFeedback(ex.ErrorKind, ex.SelectionMode);
        }
        catch (Exception)
        {
            var mode = string.IsNullOrWhiteSpace(_ttsVoice)
                ? TtsTextSelector.SelectionAuto
                : TtsTextSelector.SelectionManual;
            ShowSpeakFailureFeedback(TtsSpeakException.Protocol, mode);
        }
        finally
        {
            RefreshSpeakButton();
            if (!string.IsNullOrEmpty(successHint))
                ShowTransientStatus(successHint, FloatingStatusKind.Success);
            ResetAutoHideTimer();
        }
    }
    private Task StopTtsAsync()
    {
        if (_tts is null)
            return Task.CompletedTask;
        return _tts.StopAsync(TtsPlaybackOwner.FloatingResult);
    }

    private void RefreshSpeakButton()
    {
        if (SpeakButton is null)
            return;

        var busy = _tts?.IsBusy(TtsPlaybackOwner.FloatingResult) == true || _isTtsBusy;
        _isTtsBusy = busy;
        var sourceText = string.IsNullOrWhiteSpace(_speechText) ? _rawText : _speechText;
        var canSpeak = TtsTextSelector.CanSpeak(_modeStatus, sourceText, _ttsEnabled);

        if (busy)
        {
            SpeakButton.Content = StopIcon;
            SpeakButton.ToolTip = "停止朗读";
            SpeakButton.IsEnabled = true;
            SpeakButton.Opacity = 1.0;
            return;
        }

        SpeakButton.Content = SpeakIcon;
        SpeakButton.ToolTip = "朗读结果";
        SpeakButton.IsEnabled = canSpeak;
        SpeakButton.Opacity = canSpeak ? 1.0 : 0.45;
    }


    private void ShowSpeakFailureFeedback(string? errorKind = null, string? selectionMode = null)
    {
        var mode = selectionMode
            ?? (string.IsNullOrWhiteSpace(_ttsVoice)
                ? TtsTextSelector.SelectionAuto
                : TtsTextSelector.SelectionManual);
        var message = TtsSpeakException.UserFacingMessage(
            errorKind ?? TtsSpeakException.Protocol,
            mode);
        ShowTransientStatus(message, FloatingStatusKind.Error);
    }
    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        IsPinned = !IsPinned;
        UpdatePinVisual();
        ResetAutoHideTimer();
    }

    private void StatusMessageActionButton_Click(object sender, RoutedEventArgs e)
    {
        var action = _statusAction;
        action?.Invoke();
        ResetAutoHideTimer();
    }

    private void ResumeAutoScrollFromStatus()
    {
        _autoScroll.Resume();
        UpdateAutoScrollAffordance();
        ScrollToEndProgrammatically();
        RaiseScrollStateChanged();
    }

    private void TranslationScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        RevealScrollBarTemporarily();
        if (e.Delta > 0)
        {
            _autoScroll.PauseForUpwardNavigation();
            UpdateAutoScrollAffordance();
            RaiseScrollStateChanged();
        }
    }

    private void TranslationScroller_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            RevealScrollBarTemporarily();

        if (e.Key is Key.Up or Key.PageUp or Key.Home)
        {
            _autoScroll.PauseForUpwardNavigation();
            UpdateAutoScrollAffordance();
            RaiseScrollStateChanged();
        }
    }

    private void TranslationScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
            UpdateCurrentConversationNodeFromViewport();

        if (_isProgrammaticScroll || e.VerticalChange == 0)
            return;

        RevealScrollBarTemporarily();
        _autoScroll.OnUserScrollPositionChanged(TranslationScroller.VerticalOffset, TranslationScroller.ViewportHeight, TranslationScroller.ExtentHeight);
        UpdateAutoScrollAffordance();
        RaiseScrollStateChanged();
    }

    private void RestoreScrollState(double offset, bool autoScrollEnabled)
    {
        if (autoScrollEnabled)
        {
            ScrollToEndProgrammatically();
            return;
        }

        _isProgrammaticScroll = true;
        try { TranslationScroller.ScrollToVerticalOffset(Math.Max(0, offset)); }
        finally { _isProgrammaticScroll = false; }
        UpdateCurrentConversationNodeFromViewport();
    }

    private void ScrollToEndProgrammatically()
    {
        _isProgrammaticScroll = true;
        try { TranslationScroller.ScrollToEnd(); }
        finally { _isProgrammaticScroll = false; }
        UpdateCurrentConversationNodeFromViewport();
    }

    private void RaiseScrollStateChanged()
    {
        if (_sessionId == Guid.Empty)
            return;

        ScrollStateChanged?.Invoke(_sessionId, _activeMode, TranslationScroller.VerticalOffset, _autoScroll.IsAutoScrollEnabled);
    }

    private void RevealScrollBarTemporarily()
    {
        TranslationScroller.Tag = true;
        _scrollBarHideTimer.Stop();
        _scrollBarHideTimer.Start();
    }

    private void UpdateAutoScrollAffordance()
    {
        if (_autoScroll.IsAutoScrollEnabled)
        {
            if (_persistentStatus?.Token == FloatingStatusMessage.AutoScrollToken)
            {
                _persistentStatus = null;
                if (_transientStatus is null)
                    RenderStatusBar();
            }
            return;
        }

        _persistentStatus = new StatusMessageEntry(
            FloatingStatusMessage.AutoScrollToken,
            "自动滚动已暂停",
            FloatingStatusKind.Info,
            "恢复",
            ResumeAutoScrollFromStatus);

        if (_transientStatus is null)
            RenderStatusBar();
    }

    private void ShowTransientStatus(string message, FloatingStatusKind kind, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(message) || StatusMessageBar is null)
            return;

        _transientStatus = new StatusMessageEntry(
            FloatingStatusMessage.TransientToken,
            message.Trim(),
            kind,
            actionText: null,
            action: null);

        var timer = EnsureStatusTimer();
        timer.Stop();
        timer.Interval = FloatingStatusMessage.ResolveDuration(kind, duration);
        timer.Start();
        RenderStatusBar();
    }

    internal void ShowAnalysisFollowUpFeedback(string message) =>
        ShowTransientStatus(message, FloatingStatusKind.Warning);

    private void ClearAllStatusMessages()
    {
        _statusMessageTimer?.Stop();
        _persistentStatus = null;
        _transientStatus = null;
        _statusAction = null;
        RenderStatusBar();
    }

    private DispatcherTimer EnsureStatusTimer()
    {
        if (_statusMessageTimer is not null)
            return _statusMessageTimer;

        _statusMessageTimer = new DispatcherTimer();
        _statusMessageTimer.Tick += (_, _) =>
        {
            _statusMessageTimer.Stop();
            _transientStatus = null;
            RenderStatusBar();
        };
        return _statusMessageTimer;
    }

    private void RenderStatusBar()
    {
        if (StatusMessageBar is null || StatusMessageText is null || StatusMessageActionButton is null)
            return;

        var entry = _transientStatus ?? _persistentStatus;
        if (entry is null)
        {
            StatusMessageBar.Visibility = Visibility.Collapsed;
            StatusMessageText.Text = string.Empty;
            StatusMessageActionButton.Visibility = Visibility.Collapsed;
            StatusMessageActionButton.Content = string.Empty;
            _statusAction = null;
            return;
        }

        var (bg, fg) = FloatingStatusMessage.GetColors(entry.Kind);
        StatusMessageBar.Background = new SolidColorBrush(bg);
        StatusMessageText.Foreground = new SolidColorBrush(fg);
        StatusMessageText.Text = entry.Message;
        StatusMessageBar.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(entry.ActionText) && entry.Action is not null)
        {
            StatusMessageActionButton.Content = entry.ActionText;
            StatusMessageActionButton.Visibility = Visibility.Visible;
            _statusAction = entry.Action;
        }
        else
        {
            StatusMessageActionButton.Content = string.Empty;
            StatusMessageActionButton.Visibility = Visibility.Collapsed;
            _statusAction = null;
        }

        EnsureFooterFitsWindow();
    }

    /// <summary>
    /// Keeps Auto footer rows (status / expand) visible when the user has shrunk the window.
    /// Body (*) yields first; under manual sizing we only raise MinHeight/Height by chrome+footer, not full document height.
    /// </summary>
    private void EnsureFooterFitsWindow()
    {
        if (!IsLoaded)
            return;

        UpdateLayout();

        // Outer Border: Padding 8*2 + Margin 4*2.
        const double borderVerticalChrome = 24;
        const double bodyMinHeight = 40;

        var title = TitleBar?.ActualHeight ?? 0;
        if (TitleBar is not null)
            title += TitleBar.Margin.Top + TitleBar.Margin.Bottom;

        var status = 0.0;
        if (StatusMessageBar is { Visibility: Visibility.Visible })
        {
            status = StatusMessageBar.ActualHeight + StatusMessageBar.Margin.Top + StatusMessageBar.Margin.Bottom;
            // First layout pass can still report 0 right after Visibility flip.
            if (status < 1)
                status = 34;
        }

        var followUp = 0.0;
        if (AnalysisFollowUpInput is { Visibility: Visibility.Visible })
        {
            followUp = AnalysisFollowUpInput.ActualHeight +
                AnalysisFollowUpInput.Margin.Top +
                AnalysisFollowUpInput.Margin.Bottom;
            if (followUp < 1)
                followUp = 39;
        }

        var expand = 0.0;
        if (ExpandMarkdownButton is { Visibility: Visibility.Visible })
        {
            expand = ExpandMarkdownButton.ActualHeight + ExpandMarkdownButton.Margin.Top + ExpandMarkdownButton.Margin.Bottom;
            if (expand < 1)
                expand = 26;
        }

        var needed = borderVerticalChrome + title + bodyMinHeight + followUp + status + expand;
        if (needed <= 0 || double.IsNaN(needed))
            return;

        var minHeight = Math.Max(DefaultWindowMinHeight, needed);
        if (Math.Abs(MinHeight - minHeight) > 0.5)
            MinHeight = minHeight;

        // Auto height already grows with content; manual (user-resized) may need an explicit bump.
        if (SizeToContent == SizeToContent.Manual
            && !double.IsNaN(Height)
            && Height + 0.5 < minHeight)
        {
            Height = minHeight;
        }
    }

    private sealed class StatusMessageEntry
    {
        public StatusMessageEntry(
            string token,
            string message,
            FloatingStatusKind kind,
            string? actionText,
            Action? action)
        {
            Token = token;
            Message = message;
            Kind = kind;
            ActionText = actionText;
            Action = action;
        }

        public string Token { get; }
        public string Message { get; }
        public FloatingStatusKind Kind { get; }
        public string? ActionText { get; }
        public Action? Action { get; }
    }

    private sealed record ConversationNodeView(
        string Key,
        Button Button,
        FrameworkElement Target,
        FrameworkElement FocusTarget,
        bool IsStreaming);

    private void SetActiveModeButton(ContentType activeMode)
    {
        _activeMode = activeMode;
        TranslationModeButton.Tag = activeMode == ContentType.Translation ? "ActiveTranslation" : null;
        CodeModeButton.Tag = activeMode == ContentType.Code ? "ActiveCode" : null;
        TermModeButton.Tag = activeMode == ContentType.Term ? "ActiveTerm" : null;
        AnalysisModeButton.Tag = activeMode == ContentType.Analysis ? "ActiveAnalysis" : null;
    }

    private void UpdatePinVisual()
    {
        PinButton.Background = IsPinned ? new SolidColorBrush(Color.FromRgb(0x4A, 0x5E, 0x91)) : Brushes.Transparent;
        PinButton.ToolTip = IsPinned ? "取消固定" : "固定窗口";
    }

    private bool CanAutoHide() =>
        !IsPinned &&
        !_isLoading &&
        !_isMouseInside &&
        !_isSystemSizing &&
        !_isTtsBusy &&
        !FollowUpTextBox.IsKeyboardFocusWithin &&
        _analysisConversation.Turns.LastOrDefault()?.Status != AnalysisFollowUpTurnStatus.Loading;

    private void ResetAutoHideTimer()
    {
        _autoHideTimer.Stop();
        if (CanAutoHide())
            _autoHideTimer.Start();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            FollowUpTextBox.IsKeyboardFocusWithin &&
            !_isImeComposing &&
            e.ImeProcessedKey == Key.None &&
            Keyboard.Modifiers == ModifierKeys.None)
        {
            SubmitFollowUp();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        HideRequested?.Invoke();
        Hide();
    }

    private void FollowUpTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FollowUpInputHint.Visibility = string.IsNullOrEmpty(FollowUpTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!_suppressDraftEvent && _sessionId != Guid.Empty)
            AnalysisDraftChanged?.Invoke(_sessionId, FollowUpTextBox.Text);
        ResetAutoHideTimer();
    }

    private void FollowUpSendButton_Click(object sender, RoutedEventArgs e) => SubmitFollowUp();

    private void SubmitFollowUp()
    {
        if (!FollowUpTextBox.IsEnabled || !FollowUpSendButton.IsEnabled)
            return;

        try
        {
            var question = AnalysisConversationFormatter.NormalizeQuestion(FollowUpTextBox.Text);
            AnalysisFollowUpRequested?.Invoke(question);
        }
        catch (ArgumentException ex)
        {
            var message = ex.Message.StartsWith("追问不能超过", StringComparison.Ordinal)
                ? $"追问不能超过 {AnalysisConversationFormatter.MaxQuestionRunes} 个字符"
                : "请输入追问内容";
            ShowTransientStatus(message, FloatingStatusKind.Warning);
        }
        ResetAutoHideTimer();
    }

    private void OnFollowUpTextInputStart(object sender, TextCompositionEventArgs e) => _isImeComposing = true;
    private void OnFollowUpTextInputUpdate(object sender, TextCompositionEventArgs e) => _isImeComposing = true;
    private void OnFollowUpTextInputCompleted(object sender, TextCompositionEventArgs e) => _isImeComposing = false;

    private void PositionWindowAtAnchor()
    {
        if (_isDragging || _userMoved || _userResized || !_hasAnchor || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var workArea = Win32Api.GetPhysicalWorkAreaAtPoint(_anchor.PreferredPoint);
        if (workArea.IsEmpty)
            return;

        var physicalSize = DpiHelper.LogicalSizeToPhysical(new Size(ActualWidth, ActualHeight), _anchor.PreferredPoint);
        var scale = DpiHelper.GetScaleForPhysicalPoint(_anchor.PreferredPoint);
        var gap = PlacementGapDip * scale.Y;
        var exclusionBounds = _anchor.GetEffectiveExclusionBounds(scale);
        var rect = FloatingWindowPlacement.Calculate(_anchor.PreferredPoint, exclusionBounds, physicalSize, workArea, _placeAbove, gap);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        Win32Api.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(rect.Left), (int)Math.Round(rect.Top), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height), 0x0004 | 0x0010);
    }

    private void FloatingWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var style = Win32Api.GetWindowLongPtr(hwnd, Win32Api.GWL_STYLE).ToInt64();
        if ((style & Win32Api.WS_THICKFRAME) == 0)
        {
            Win32Api.SetWindowLongPtr(hwnd, Win32Api.GWL_STYLE, new IntPtr(style | Win32Api.WS_THICKFRAME));
            const uint swpNoMove = 0x0002;
            const uint swpNoSize = 0x0001;
            const uint swpNoZOrder = 0x0004;
            const uint swpFrameChanged = 0x0020;
            Win32Api.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, swpNoMove | swpNoSize | swpNoZOrder | swpFrameChanged);
        }

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(FloatingWindowWindowProc);
    }

    private IntPtr FloatingWindowWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmEnterSizeMove)
        {
            // Keep the current auto-sized height as the starting point for manual resizing.
            SizeToContent = SizeToContent.Manual;
            _isSystemSizing = true;
            _autoHideTimer.Stop();
            TranslationScroller.MaxHeight = double.PositiveInfinity;
            return IntPtr.Zero;
        }

        if (msg == WmExitSizeMove)
        {
            _userResized = true;
            _isSystemSizing = false;
            UpdateLayout();
            ResetAutoHideTimer();
            return IntPtr.Zero;
        }

        if (msg != WmNchittest || _isDragging || !IsHitTestVisible)
            return IntPtr.Zero;

        var screenX = unchecked((short)(long)lParam);
        var screenY = unchecked((short)((long)lParam >> 16));
        if (!Win32Api.GetWindowRect(hwnd, out var windowRect))
            return IntPtr.Zero;

        var border = ResizeBorderPhysical;
        var left = screenX - windowRect.Left < border;
        var right = windowRect.Right - screenX <= border;
        var top = screenY - windowRect.Top < border;
        var bottom = windowRect.Bottom - screenY <= border;

        var hit = (left, right, top, bottom) switch
        {
            (true, false, true, false) => HtTopLeft,
            (false, true, true, false) => HtTopRight,
            (true, false, false, true) => HtBottomLeft,
            (false, true, false, true) => HtBottomRight,
            (_, false, true, false) => HtTop,
            (_, false, false, true) => HtBottom,
            (true, false, _, _) => HtLeft,
            (false, true, _, _) => HtRight,
            _ => 1
        };

        if (hit != 1)
        {
            handled = true;
            return new IntPtr(hit);
        }

        return IntPtr.Zero;
    }

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInsideButton(e.OriginalSource as DependencyObject))
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero ||
            !Win32Api.GetCursorPos(out var cursor) ||
            !Win32Api.GetWindowRect(hwnd, out var windowRect))
        {
            return;
        }

        _dragStartCursorPhysical = new Point(cursor.X, cursor.Y);
        _dragStartWindowPhysical = new Point(windowRect.Left, windowRect.Top);
        if (!Mouse.Capture(TitleBar, CaptureMode.Element))
            return;

        _isDragging = true;
        _autoHideTimer.Stop();
        e.Handled = true;
    }

    private void TitleBar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDragging();
            return;
        }

        if (!Win32Api.GetCursorPos(out var cursor))
            return;

        var currentCursorPhysical = new Point(cursor.X, cursor.Y);
        var deltaX = currentCursorPhysical.X - _dragStartCursorPhysical.X;
        var deltaY = currentCursorPhysical.Y - _dragStartCursorPhysical.Y;
        var newLeft = _dragStartWindowPhysical.X + deltaX;
        var newTop = _dragStartWindowPhysical.Y + deltaY;

        var workArea = Win32Api.GetPhysicalWorkAreaAtPoint(currentCursorPhysical);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!workArea.IsEmpty && hwnd != IntPtr.Zero && Win32Api.GetWindowRect(hwnd, out var windowRect))
        {
            var width = windowRect.Right - windowRect.Left;
            var height = windowRect.Bottom - windowRect.Top;
            newLeft = Math.Clamp(newLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
            newTop = Math.Clamp(newTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));

            const uint swpNoSize = 0x0001;
            const uint swpNoZOrder = 0x0004;
            const uint swpNoActivate = 0x0010;
            Win32Api.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                (int)Math.Round(newLeft),
                (int)Math.Round(newTop),
                0,
                0,
                swpNoSize | swpNoZOrder | swpNoActivate);
        }

        if (Math.Abs(deltaX) + Math.Abs(deltaY) > 2)
            _userMoved = true;
    }

    private void TitleBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;

        EndDragging();
    }

    private void TitleBar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDragging)
            EndDragging();
    }

    private void EndDragging(bool resetAutoHideTimer = true)
    {
        _isDragging = false;
        if (Mouse.Captured == TitleBar)
            Mouse.Capture(null);
        if (resetAutoHideTimer)
            ResetAutoHideTimer();
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is Button)
                return true;
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(child);
        return LogicalTreeHelper.GetParent(child);
    }

    private static async Task WaitForCompositionFrameAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            completion.TrySetResult(true);
        };
        CompositionTarget.Rendering += handler;
        try { await Task.WhenAny(completion.Task, Task.Delay(100)); }
        finally { CompositionTarget.Rendering -= handler; }
    }
}

