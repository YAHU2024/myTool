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

namespace QuickTranslate.UI;

/// <summary>
/// A reusable result window positioned beside the source selection.
/// Request ownership remains outside this view; the window only reports intent and view state.
/// </summary>
public partial class FloatingWindow : Window
{
    private const double PlacementGapDip = 12;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly LatestPresentationCoordinator _presentations = new();
    private readonly AutoScrollController _autoScroll = new();
    private DispatcherTimer? _detectionHintTimer;
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
    private FloatingWindowAnchor _anchor;
    private bool _hasAnchor;
    private bool _placeAbove;
    private Guid _sessionId;
    private ContentType _activeMode = ContentType.Translation;

    public event Action<ContentType>? ModeRequested;
    public event Action? RefreshRequested;
    public event Action? DismissRequested;
    public event Action<Guid, ContentType, double, bool>? ScrollStateChanged;

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

    public new void Hide()
    {
        EndDragging(resetAutoHideTimer: false);
        _userMoved = false;
        _userResized = false;
        _isSystemSizing = false;
        SizeToContent = SizeToContent.Height;
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
        _sessionId = sessionId;
        _activeMode = mode;
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
        _isLoading = isLoading;
        LoadingIndicator.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        if (isLoading)
            ((Storyboard)Resources["LoadingDotsStoryboard"]).Begin(this, true);
        else
            ((Storyboard)Resources["LoadingDotsStoryboard"]).Remove(this);
        ResetAutoHideTimer();
    }

    public void ResetPin()
    {
        IsPinned = false;
        UpdatePinVisual();
        ResetAutoHideTimer();
    }

    internal void ShowDetectionHint(DetectionResult? detection)
    {
        ClearDetectionHint();
        if (detection is not { Confidence: DetectionConfidence.Low } ||
            detection.ContentType is not (ContentType.Code or ContentType.Term))
        {
            return;
        }

        DetectionHintText.Text = detection.ContentType == ContentType.Code
            ? "识别为命令；结果不对可切换到翻译"
            : "识别为术语；结果不对可切换到翻译";
        DetectionHint.Visibility = Visibility.Visible;
        _detectionHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _detectionHintTimer.Tick += (_, _) =>
        {
            ClearDetectionHint();
        };
        _detectionHintTimer.Start();
    }

    public void ClearDetectionHint()
    {
        _detectionHintTimer?.Stop();
        _detectionHintTimer = null;
        DetectionHint.Visibility = Visibility.Collapsed;
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
            ShowCopyFeedback(button, "复制代码");
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
        _autoHideTimer.Stop();
        _isMouseInside = false;
        _sessionId = Guid.Empty;
        _activeMode = ContentType.Translation;
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
                ShowCopyFeedback(btn, "\u29C9", 11);
        }
        catch { /* Clipboard access can be temporarily unavailable. */ }
        ResetAutoHideTimer();
    }

    private static void ShowCopyFeedback(Button button, object originalContent, double? feedbackFontSize = null)
    {
        button.Content = feedbackFontSize.HasValue
            ? new TextBlock { Text = "\u2714", FontSize = feedbackFontSize.Value }
            : (object)"\u2714";
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

    private void ResumeScrollButton_Click(object sender, RoutedEventArgs e)
    {
        _autoScroll.Resume();
        UpdateAutoScrollAffordance();
        ScrollToEndProgrammatically();
        RaiseScrollStateChanged();
    }

    private void TranslationScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            _autoScroll.PauseForUpwardNavigation();
            UpdateAutoScrollAffordance();
            RaiseScrollStateChanged();
        }
    }

    private void TranslationScroller_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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

    private void UpdateAutoScrollAffordance() =>
        ResumeScrollButton.Visibility = _autoScroll.IsAutoScrollEnabled ? Visibility.Collapsed : Visibility.Visible;

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

    private bool CanAutoHide() => !IsPinned && !_isLoading && !_isMouseInside && !_isSystemSizing;

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
        DismissRequested?.Invoke();
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
