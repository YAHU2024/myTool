using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Diagnostics;
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

/// <summary>
/// A per-answer, transient reasoning view. It owns its Markdown renderer and
/// never exposes its content to copy, TTS, history, cache, or follow-up data.
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
    private StreamingMarkdownRenderer? _renderer;
    private bool _userToggled;
    private string _rawText = string.Empty;
    private bool _truncated;
    private long _startedTimestamp;
    private ThoughtBlockStatus _status;
    private bool _disposed;

    public ThoughtBlockView()
    {
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimer.Tick += ElapsedTimer_Tick;

        Root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x25, 0x35)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x49)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed
        };

        _body = new StackPanel();
        var header = new Grid { Margin = new Thickness(8, 5, 5, 5) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xC5, 0xFF)),
            FontFamily = new FontFamily(MarkdownRenderer.ConversationFontFamilyName),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(_statusText);

        _toggleButton = new Button
        {
            Content = ChevronUp,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Width = 24,
            Height = 22,
            Padding = new Thickness(0),
            Focusable = false,
            IsTabStop = false,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xD7, 0xDE)),
            ToolTip = "收起思考"
        };
        AutomationProperties.SetName(_toggleButton, "收起思考");
        _toggleButton.Click += ToggleButton_Click;
        Grid.SetColumn(_toggleButton, 1);
        header.Children.Add(_toggleButton);
        _body.Children.Add(header);

        _stableHost = CreateMarkdownHost();
        _activeTextHost = new TextBox
        {
            Visibility = Visibility.Collapsed,
            FontFamily = new FontFamily(MarkdownRenderer.ConversationFontFamilyName),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xBD, 0xBD, 0xCA)),
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            Focusable = false,
            IsTabStop = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 0, 8, 8),
            IsHitTestVisible = false
        };
        _activeHost = CreateMarkdownHost();
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
        _body.Children.Add(_scrollViewer);
        Root.Child = _body;
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
                SuppressInteractiveMarkdownActions(_renderer.Document);
                SuppressInteractiveMarkdownActions(_renderer.ActiveDocument);
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
        }

        UpdateStatusText();
        if (status is ThoughtBlockStatus.Completed or ThoughtBlockStatus.Cancelled or ThoughtBlockStatus.Failed)
        {
            _elapsedTimer.Stop();
        }

        if (status is (ThoughtBlockStatus.Completed or ThoughtBlockStatus.Cancelled or ThoughtBlockStatus.Failed) && !_userToggled)
            SetExpanded(false);
    }

    public void Complete(bool truncated) => Update(_rawText, truncated, ThoughtBlockStatus.Completed);

    public void Cancel(bool truncated) => Update(_rawText, truncated, ThoughtBlockStatus.Cancelled);

    public void Fail(bool truncated) => Update(_rawText, truncated, ThoughtBlockStatus.Failed);

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
    }

    private void SetExpanded(bool expanded)
    {
        _scrollViewer.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _toggleButton.Content = expanded ? ChevronUp : ChevronDown;
        var label = expanded ? "收起思考" : "展开思考";
        _toggleButton.ToolTip = label;
        AutomationProperties.SetName(_toggleButton, label);
    }

    private static RichTextBox CreateMarkdownHost() => new()
    {
        IsReadOnly = true,
        IsUndoEnabled = false,
        IsReadOnlyCaretVisible = false,
        IsDocumentEnabled = false,
        Focusable = false,
        IsTabStop = false,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Padding = new Thickness(8, 0, 8, 8),
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        IsHitTestVisible = false
    };

    private static void SuppressInteractiveMarkdownActions(DependencyObject? root)
    {
        if (root is null)
            return;

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button button)
                button.Visibility = Visibility.Collapsed;
            SuppressInteractiveMarkdownActions(child);
        }
    }
}
