using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickTranslate.Core;
using QuickTranslate.Helpers;

namespace QuickTranslate.UI;

internal enum ThoughtBlockStatus
{
    Thinking,
    Streaming,
    Completed,
    Cancelled,
    Failed
}

internal sealed record ThoughtBlockSnapshot(
    string RawText,
    bool IsTruncated,
    ThoughtBlockStatus Status,
    TimeSpan Elapsed,
    bool IsExpanded,
    bool UserToggled);

/// <summary>
/// A per-answer, transient reasoning view. It owns its Markdown renderer and
/// keeps its content outside TTS, history, cache, logs, and follow-up data while
/// still exposing the same deliberate Markdown selection behavior as the answer.
/// </summary>
internal sealed class ThoughtBlockView
{
    private const double MaxHeight = 150;
    private const string ChevronDown = "\uE70D";
    private const string ChevronUp = "\uE70E";

    private readonly StackPanel _body;
    private readonly Button _toggleButton;
    private readonly TextBlock _statusText;
    private readonly ScrollViewer _scrollViewer;
    private readonly RichTextBox _stableHost;
    private readonly TextBox _activeTextHost;
    private readonly RichTextBox _activeHost;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly List<RichTextBox> _markdownHosts;
    private StreamingMarkdownRenderer? _renderer;
    private bool _userToggled;
    private string _rawText = string.Empty;
    private bool _truncated;
    private long _startedTimestamp;
    private ThoughtBlockStatus _status;
    private bool _disposed;
    private bool _autoFollow = true;
    private bool _isProgrammaticScroll;
    private bool _isPointerDown;
    private string? _pendingRawText;
    private bool _pendingTruncated;
    private ThoughtBlockStatus _pendingStatus;

    public ThoughtBlockView()
    {
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimer.Tick += ElapsedTimer_Tick;

        Root = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x49)),
            BorderThickness = new Thickness(2, 0, 0, 1),
            Margin = new Thickness(0, 0, 0, 6),
            Visibility = Visibility.Collapsed
        };

        _body = new StackPanel();
        var header = new Grid { Margin = new Thickness(6, 3, 2, 3) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xAE, 0xB4, 0xC2)),
            FontFamily = new FontFamily(MarkdownRenderer.ConversationFontFamilyName),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(_statusText);

        _toggleButton = new Button
        {
            Content = ChevronUp,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xD7, 0xDE)),
            ToolTip = "收起思考"
        };
        _toggleButton.SetResourceReference(FrameworkElement.StyleProperty, "ThoughtChevronButton");
        AutomationProperties.SetName(_toggleButton, "收起思考");
        _toggleButton.Click += ToggleButton_Click;
        Grid.SetColumn(_toggleButton, 1);
        header.Children.Add(_toggleButton);
        _body.Children.Add(header);

        _stableHost = CreateMarkdownHost("思考内容");
        _activeTextHost = new TextBox
        {
            Visibility = Visibility.Collapsed,
            FontFamily = new FontFamily(MarkdownRenderer.ConversationFontFamilyName),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xBD, 0xBD, 0xCA)),
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            Focusable = true,
            IsTabStop = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 0, 8, 8),
            IsHitTestVisible = true,
            SelectionBrush = new SolidColorBrush(Color.FromRgb(0x4D, 0xB6, 0xAC)),
            SelectionOpacity = 0.45,
            Cursor = Cursors.IBeam
        };
        _activeHost = CreateMarkdownHost("思考内容");
        _markdownHosts = [_stableHost, _activeHost];
        var content = new StackPanel();
        content.Children.Add(_stableHost);
        content.Children.Add(_activeTextHost);
        content.Children.Add(_activeHost);
        _scrollViewer = new ScrollViewer
        {
            Content = content,
            MaxHeight = MaxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            IsTabStop = false,
            Padding = new Thickness(0)
        };
        _scrollViewer.SetResourceReference(FrameworkElement.StyleProperty, "Win11ScrollViewer");
        _scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
        _scrollViewer.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
        _scrollViewer.PreviewMouseLeftButtonDown += ScrollViewer_PreviewMouseLeftButtonDown;
        _scrollViewer.PreviewMouseLeftButtonUp += ScrollViewer_PreviewMouseLeftButtonUp;
        _body.Children.Add(_scrollViewer);
        Root.Child = _body;
        ConfigureMarkdownSelection(_stableHost);
        ConfigureMarkdownSelection(_activeHost);
        ConfigureTextSelection(_activeTextHost);
    }

    public Border Root { get; }

    public void DetachFromParent()
    {
        if (Root.Parent is Panel panel)
            panel.Children.Remove(Root);
    }

    internal TextBlock StatusTextForTests => _statusText;

    internal Button ToggleButtonForTests => _toggleButton;

    internal bool IsExpandedForTests => _scrollViewer.Visibility == Visibility.Visible;

    internal bool IsElapsedTimerEnabledForTests => _elapsedTimer.IsEnabled;

    internal RichTextBox StableMarkdownHostForTests => _stableHost;

    internal TextBox ActiveTextHostForTests => _activeTextHost;

    internal ScrollViewer ScrollViewerForTests => _scrollViewer;

    internal bool IsAutoFollowEnabledForTests => _autoFollow;

    public bool IsVisible => Root.Visibility == Visibility.Visible;

    public void Begin()
    {
        _disposed = false;
        _elapsedTimer.Stop();
        _rawText = string.Empty;
        _truncated = false;
        _userToggled = false;
        _startedTimestamp = Stopwatch.GetTimestamp();
        _status = ThoughtBlockStatus.Thinking;
        _renderer = null;
        _autoFollow = true;
        _pendingRawText = null;
        _isPointerDown = false;
        UpdateStatusText();
        SetExpanded(true);
        Root.Visibility = Visibility.Collapsed;
        _elapsedTimer.Start();
    }

    public void HideIfEmpty()
    {
        if (string.IsNullOrWhiteSpace(_rawText))
            Root.Visibility = Visibility.Collapsed;
    }

    public void Update(string rawText, bool truncated, ThoughtBlockStatus status)
    {
        if (_disposed)
            return;

        _status = status;
        _truncated = truncated;
        if (IsMarkdownInteractionActive && !string.IsNullOrWhiteSpace(rawText))
        {
            _pendingRawText = rawText;
            _pendingTruncated = truncated;
            _pendingStatus = status;
            UpdateStatusText();
            StopElapsedTimerIfTerminal(status);
            return;
        }

        ApplyUpdate(rawText);
        UpdateStatusText();
        StopElapsedTimerIfTerminal(status);

        if (status is (ThoughtBlockStatus.Completed or ThoughtBlockStatus.Cancelled or ThoughtBlockStatus.Failed) && !_userToggled)
            SetExpanded(false);
    }

    private void ApplyUpdate(string rawText)
    {
        if (!string.IsNullOrWhiteSpace(rawText))
        {
            _rawText = rawText;
            _renderer ??= new StreamingMarkdownRenderer(
                MarkdownRenderer.ConversationFontSize - 2,
                int.MaxValue,
                separateActiveDocument: true);
            var rendered = _renderer.Update(rawText);
            if (!rendered)
            {
                _activeTextHost.Text = rawText;
                _activeTextHost.Visibility = Visibility.Visible;
                _stableHost.Visibility = Visibility.Collapsed;
                _activeHost.Visibility = Visibility.Collapsed;
            }
            else
            {
                _stableHost.Document = _renderer.Document;
                _activeHost.Document = _renderer.ActiveDocument!;
                _stableHost.Visibility = _renderer.HasStableBlocks
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                _activeHost.Visibility = _renderer.HasActiveBlocks
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                var activeText = _renderer.ActivePlainText;
                _activeTextHost.Text = activeText ?? string.Empty;
                _activeTextHost.Visibility = activeText is not null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            Root.Visibility = Visibility.Visible;
            RequestScrollToLatest();
        }
    }

    public void Complete(bool truncated) => Update(_rawText, truncated, ThoughtBlockStatus.Completed);

    public void Cancel(bool truncated) => Update(_rawText, truncated, ThoughtBlockStatus.Cancelled);

    public void Fail(bool truncated) => Update(_rawText, truncated, ThoughtBlockStatus.Failed);

    internal ThoughtBlockSnapshot? CaptureForModeSwitch()
    {
        if (_pendingRawText is { } pendingRawText)
        {
            _rawText = pendingRawText;
            _truncated = _pendingTruncated;
            _status = _pendingStatus;
            _pendingRawText = null;
        }

        if (string.IsNullOrWhiteSpace(_rawText))
            return null;

        if (_status is ThoughtBlockStatus.Thinking or ThoughtBlockStatus.Streaming)
        {
            _status = ThoughtBlockStatus.Cancelled;
            _elapsedTimer.Stop();
            UpdateStatusText();
            if (!_userToggled)
                SetExpanded(false);
        }

        return CreateSnapshot();
    }

    internal ThoughtBlockSnapshot? CaptureSnapshot() =>
        string.IsNullOrWhiteSpace(_rawText) ? null : CreateSnapshot();

    internal void Restore(ThoughtBlockSnapshot snapshot)
    {
        Begin();
        _startedTimestamp = Stopwatch.GetTimestamp() -
            (long)(snapshot.Elapsed.TotalSeconds * Stopwatch.Frequency);
        _userToggled = snapshot.UserToggled;
        Update(snapshot.RawText, snapshot.IsTruncated, snapshot.Status);
        SetExpanded(snapshot.IsExpanded);
    }

    private ThoughtBlockSnapshot CreateSnapshot() => new(
        _rawText,
        _truncated,
        _status,
        _startedTimestamp == 0
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(_startedTimestamp),
        IsExpandedForTests,
        _userToggled);

    private void StopElapsedTimerIfTerminal(ThoughtBlockStatus status)
    {
        if (status is ThoughtBlockStatus.Completed or ThoughtBlockStatus.Cancelled or ThoughtBlockStatus.Failed)
            _elapsedTimer.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _elapsedTimer.Stop();
        _elapsedTimer.Tick -= ElapsedTimer_Tick;
        Root.Visibility = Visibility.Collapsed;
    }

    private void ElapsedTimer_Tick(object? sender, EventArgs e) => UpdateStatusText();

    private void UpdateStatusText()
    {
        var elapsed = _startedTimestamp == 0
            ? string.Empty
            : $" {Stopwatch.GetElapsedTime(_startedTimestamp).TotalSeconds:0.0} 秒";
        _statusText.Text = _status switch
        {
            ThoughtBlockStatus.Cancelled => $"思考已停止 ·{elapsed}",
            ThoughtBlockStatus.Failed => $"思考中断 ·{elapsed}",
            ThoughtBlockStatus.Completed => _truncated
                ? $"思考了{elapsed} · 内容已截断"
                : $"思考了{elapsed}",
            _ => $"正在思考…{elapsed}"
        };
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _userToggled = true;
        SetExpanded(!_scrollViewer.IsVisible);
        if (_scrollViewer.IsVisible)
            RequestScrollToLatest();
    }

    private void SetExpanded(bool expanded)
    {
        _scrollViewer.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _toggleButton.Content = expanded ? ChevronUp : ChevronDown;
        var label = expanded ? "收起思考" : "展开思考";
        _toggleButton.ToolTip = label;
        AutomationProperties.SetName(_toggleButton, label);
    }

    private void RequestScrollToLatest()
    {
        if (!_autoFollow || !_scrollViewer.IsVisible)
            return;

        Root.Dispatcher.BeginInvoke(() =>
        {
            if (!_autoFollow || !_scrollViewer.IsVisible)
                return;

            _isProgrammaticScroll = true;
            try
            {
                _scrollViewer.ScrollToEnd();
            }
            finally
            {
                _isProgrammaticScroll = false;
            }
        }, DispatcherPriority.Loaded);
    }

    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isProgrammaticScroll || _scrollViewer.ScrollableHeight <= 0)
            return;

        _autoFollow = _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 1;
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
            _autoFollow = false;
        else if (_scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 1)
            _autoFollow = true;
    }

    private void ScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _isPointerDown = true;

    private void ScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPointerDown = false;
        Root.Dispatcher.BeginInvoke(ApplyPendingUpdate, DispatcherPriority.ContextIdle);
    }

    private bool IsMarkdownInteractionActive =>
        _isPointerDown ||
        _markdownHosts.Any(host => host.IsKeyboardFocusWithin && !host.Selection.IsEmpty) ||
        (_activeTextHost.IsKeyboardFocusWithin && _activeTextHost.SelectionLength > 0);

    private void ConfigureMarkdownSelection(RichTextBox markdown)
    {
        markdown.PreviewMouseLeftButtonDown += Markdown_PreviewMouseLeftButtonDown;
        markdown.PreviewMouseLeftButtonUp += Markdown_PreviewMouseLeftButtonUp;
        markdown.SelectionChanged += Markdown_SelectionChanged;
        markdown.GotKeyboardFocus += Markdown_KeyboardFocusChanged;
        markdown.LostKeyboardFocus += Markdown_KeyboardFocusChanged;
        markdown.Unloaded += Markdown_Unloaded;
    }

    private void ConfigureTextSelection(TextBox textBox)
    {
        textBox.PreviewMouseLeftButtonDown += TextBox_PreviewMouseLeftButtonDown;
        textBox.PreviewMouseLeftButtonUp += TextBox_PreviewMouseLeftButtonUp;
        textBox.SelectionChanged += TextBox_SelectionChanged;
        textBox.GotKeyboardFocus += TextBox_KeyboardFocusChanged;
        textBox.LostKeyboardFocus += TextBox_KeyboardFocusChanged;
        textBox.Unloaded += TextBox_Unloaded;
    }

    private void Markdown_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isPointerDown = true;

    private void Markdown_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPointerDown = false;
        Root.Dispatcher.BeginInvoke(ApplyPendingUpdate, DispatcherPriority.ContextIdle);
    }

    private void Markdown_SelectionChanged(object sender, RoutedEventArgs e) =>
        Root.Dispatcher.BeginInvoke(ApplyPendingUpdate, DispatcherPriority.ContextIdle);

    private void Markdown_KeyboardFocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
        Root.Dispatcher.BeginInvoke(ApplyPendingUpdate, DispatcherPriority.ContextIdle);

    private void Markdown_Unloaded(object sender, RoutedEventArgs e) =>
        Root.Dispatcher.BeginInvoke(ApplyPendingUpdate, DispatcherPriority.ContextIdle);

    private void TextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isPointerDown = true;

    private void TextBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isPointerDown = false;
        Root.Dispatcher.BeginInvoke(ApplyPendingUpdate, DispatcherPriority.ContextIdle);
    }

    private void TextBox_SelectionChanged(object sender, RoutedEventArgs e) =>
        Root.Dispatcher.BeginInvoke(ApplyPendingUpdate, DispatcherPriority.ContextIdle);

    private void TextBox_KeyboardFocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
        Root.Dispatcher.BeginInvoke(ApplyPendingUpdate, DispatcherPriority.ContextIdle);

    private void TextBox_Unloaded(object sender, RoutedEventArgs e) =>
        Root.Dispatcher.BeginInvoke(ApplyPendingUpdate, DispatcherPriority.ContextIdle);

    private void ApplyPendingUpdate()
    {
        if (_pendingRawText is null || IsMarkdownInteractionActive)
            return;

        var rawText = _pendingRawText;
        var truncated = _pendingTruncated;
        var status = _pendingStatus;
        _pendingRawText = null;
        Update(rawText, truncated, status);
    }

    private RichTextBox CreateMarkdownHost(string automationName)
    {
        var markdown = new RichTextBox
        {
            FontSize = MarkdownRenderer.ConversationFontSize - 2
        };
        MarkdownInteraction.ConfigureSelectableHost(markdown, automationName);
        markdown.Padding = new Thickness(8, 0, 8, 8);
        return markdown;
    }

}
