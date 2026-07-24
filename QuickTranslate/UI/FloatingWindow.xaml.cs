using System.Diagnostics;
using System.Windows;
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
    private ITtsService? _tts;
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
    private bool _placeAbove;
    private Guid _sessionId;
    private ContentType _activeMode = ContentType.Translation;

    public event Action<ContentType>? ModeRequested;
    public event Action? RefreshRequested;
    public event Action? HideRequested;
    public event Action<Guid, ContentType, double, bool>? ScrollStateChanged;

    internal bool IsTtsBusy => _isTtsBusy;

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

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoHideTimer.Tick += (_, _) =>
        {
            if (CanAutoHide())
                Hide();
            _autoHideTimer.Stop();
        };

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

    internal void AttachTts(ITtsService tts)
    {
        if (_tts is not null)
            _tts.StateChanged -= OnTtsStateChanged;
        _tts = tts;
        _tts.StateChanged += OnTtsStateChanged;
        _isTtsBusy = _tts.IsBusy;
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

    private void OnTtsStateChanged()
    {
        var busy = _tts?.IsBusy == true;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnTtsStateChanged);
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
    internal void SetSessionView(Guid sessionId, ContentType mode, ModeResultState state)
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
        const double chromeHeightDip = 54;
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
        ShowPlainText();
        if (_isLoading && !string.IsNullOrEmpty(translation))
            HideLoadingIndicator();
        if (_autoScroll.OnContentOrViewportChanged())
            ScrollToEndProgrammatically();

        UpdateLayout();
        PositionWindowAtAnchor();
        ResetAutoHideTimer();
    }

    private void ShowPlainText()
    {
        MarkdownDocumentHost.Visibility = Visibility.Collapsed;
        ExpandMarkdownButton.Visibility = Visibility.Collapsed;
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
        UpdateLayout();
        PositionWindowAtAnchor();
        if (_autoScroll.IsAutoScrollEnabled)
            ScrollToEndProgrammatically();
    }

    private void MarkdownCodeCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Tag: MarkdownCodeBlock metadata } button)
            return;
        try
        {
            Clipboard.SetText(metadata.Code);
            ShowCopyFeedback(button, "\u29C9");
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
        _isMarkdownExpanded = false;
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
        var text = _rawText;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            if (sender is Button btn)
                ShowCopyFeedback(btn, "\u29C9");
        }
        catch { /* Clipboard access can be temporarily unavailable. */ }
        ResetAutoHideTimer();
    }

    

    private async void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        ResetAutoHideTimer();
        if (_tts is null)
            return;

        if (_tts.IsBusy || _isTtsBusy)
        {
            await StopTtsAsync().ConfigureAwait(true);
            return;
        }

        // Debounce idle re-clicks so latest-wins churn is less likely.
        var now = DateTime.UtcNow;
        if ((now - _lastSpeakClickUtc).TotalMilliseconds < 300)
            return;
        _lastSpeakClickUtc = now;

        if (!TtsTextSelector.CanSpeak(_modeStatus, _rawText, _ttsEnabled))
            return;

        var speechText = TtsTextSelector.NormalizeForSpeech(_rawText, _ttsMaxChars, out var truncated);
        if (string.IsNullOrWhiteSpace(speechText))
            return;

        if (truncated)
        {
            Logger.Warn("FloatingWindow", "tts.speak.truncated", new Dictionary<string, object?>
            {
                ["text_len"] = _rawText.Length,
                ["max_chars"] = _ttsMaxChars
            });
            ShowTransientStatus("文本过长，已截断朗读", FloatingStatusKind.Warning);
        }

        string? successHint = null;
        try
        {
            var voiceOverride = string.IsNullOrWhiteSpace(_ttsVoice) ? null : _ttsVoice;
            await _tts.SpeakAsync(
                speechText,
                languageHint: null,
                voiceOverride,
                _ttsRate,
                CancellationToken.None).ConfigureAwait(true);

            if (_tts is EdgeTtsService edgeTts)
                successHint = edgeTts.TakeLastUiHint();
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
        return _tts.StopAsync();
    }

    private void RefreshSpeakButton()
    {
        if (SpeakButton is null)
            return;

        var busy = _tts?.IsBusy == true || _isTtsBusy;
        _isTtsBusy = busy;
        var canSpeak = TtsTextSelector.CanSpeak(_modeStatus, _rawText, _ttsEnabled);

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
    private static void ShowCopyFeedback(Button button, object originalContent)
    {
        button.Content = "\u2714";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            button.Content = originalContent;
            timer.Stop();
        };
        timer.Start();
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
    }

    private void ScrollToEndProgrammatically()
    {
        _isProgrammaticScroll = true;
        try { TranslationScroller.ScrollToEnd(); }
        finally { _isProgrammaticScroll = false; }
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

    private bool CanAutoHide() => !IsPinned && !_isLoading && !_isMouseInside && !_isSystemSizing && !_isTtsBusy;

    private void ResetAutoHideTimer()
    {
        _autoHideTimer.Stop();
        if (CanAutoHide())
            _autoHideTimer.Start();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        HideRequested?.Invoke();
        Hide();
    }

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

