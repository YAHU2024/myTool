using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using QuickTranslate.Core;
using QuickTranslate.Helpers;

namespace QuickTranslate.UI;

/// <summary>截图 OCR/翻译期间的可取消状态窗，不显示识别文本或译文。</summary>
public partial class ScreenshotTranslationProgressWindow : Window
{
    private readonly ScreenshotRegion _region;
    private readonly Point _dpiScale;
    private readonly DispatcherTimer _showTimer;

    public ScreenshotTranslationProgressWindow(ScreenshotRegion region)
    {
        if (!region.IsValid)
            throw new ArgumentException("截图区域无效。", nameof(region));
        _region = region;
        _dpiScale = DpiHelper.GetScaleForPhysicalPoint(new Point(region.Left, region.Top));
        InitializeComponent();
        // The window owns this one-shot timer; closing before the delay expires
        // must prevent an already-completed session from showing stale progress.
        _showTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _showTimer.Tick += ShowTimer_Tick;
        Closed += (_, _) =>
        {
            _showTimer.Stop();
            _showTimer.Tick -= ShowTimer_Tick;
        };
    }

    public event Action? CancelRequested;

    private bool _closeForOverlay;

    public bool ClosedForOverlay => _closeForOverlay;

    public void CloseForOverlay()
    {
        _closeForOverlay = true;
        Close();
    }

    public void SetStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        StatusText.Text = message.Trim();
    }

    public void ShowProgress(TimeSpan? delay = null)
    {
        _showTimer.Interval = delay ?? TimeSpan.FromMilliseconds(250);
        _showTimer.Start();
    }

    private void ShowTimer_Tick(object? sender, EventArgs e)
    {
        _showTimer.Stop();
        Show();
        UpdateLayout();
        var work = Win32Api.GetPhysicalWorkAreaAtPoint(new Point(_region.Left, _region.Top));
        var left = (int)Math.Round(work.IsEmpty
            ? _region.Left + 24
            : work.Left + (work.Width - (int)Math.Round(ActualWidth * _dpiScale.X)) / 2);
        var top = (int)Math.Round(work.IsEmpty
            ? _region.Top + 24
            : work.Top + (work.Height - (int)Math.Round(ActualHeight * _dpiScale.Y)) / 2);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Win32Api.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            left,
            top,
            (int)Math.Round(ActualWidth * _dpiScale.X),
            (int)Math.Round(ActualHeight * _dpiScale.Y),
            0x0004 | 0x0010 | 0x0040);
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke();
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        CancelRequested?.Invoke();
        Close();
        e.Handled = true;
    }

}
