using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using QuickTranslate.Core;
using QuickTranslate.Helpers;

namespace QuickTranslate.UI;

/// <summary>
/// 单显示器截图框选遮罩。所有选区判断使用物理像素，WPF DIP 只用于绘制。
/// </summary>
public partial class ScreenshotSelectionWindow : Window
{
    private static readonly TimeSpan SelectionTimeout = TimeSpan.FromSeconds(60);
    private const string DefaultHint = "拖动鼠标框选截图区域，按 Esc 取消";
    private readonly ScreenshotRegion _monitorRegion;
    private readonly Models.OcrResourceLimits _resourceLimits;
    private readonly Point _dpiScale;
    private readonly DispatcherTimer _selectionTimeoutTimer;
    private bool _isDragging;
    private bool _completed;
    private PhysicalPoint _dragStart;
    private PhysicalPoint _dragCurrent;

    public ScreenshotSelectionWindow(
        ScreenshotRegion monitorRegion,
        Models.OcrResourceLimits? resourceLimits = null)
    {
        if (!monitorRegion.IsValid)
            throw new ArgumentException("显示器区域必须大于 0。", nameof(monitorRegion));

        _monitorRegion = monitorRegion;
        _resourceLimits = resourceLimits ?? Models.OcrResourceLimits.Default;
        _dpiScale = DpiHelper.GetScaleForPhysicalPoint(
            new Point(monitorRegion.Left, monitorRegion.Top));
        _selectionTimeoutTimer = new DispatcherTimer { Interval = SelectionTimeout };
        _selectionTimeoutTimer.Tick += (_, _) =>
        {
            _selectionTimeoutTimer.Stop();
            CancelSelection();
        };
        InitializeComponent();
        Closed += OnClosed;
    }

    public event Action<ScreenshotRegion>? SelectionCompleted;

    public event Action? Cancelled;

    public ScreenshotRegion MonitorRegion => _monitorRegion;

    public void ShowSelection()
    {
        Left = _monitorRegion.Left / _dpiScale.X;
        Top = _monitorRegion.Top / _dpiScale.Y;
        Width = _monitorRegion.Width / _dpiScale.X;
        Height = _monitorRegion.Height / _dpiScale.Y;
        Show();
        UpdateLayout();

        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Api.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            _monitorRegion.Left,
            _monitorRegion.Top,
            _monitorRegion.Width,
            _monitorRegion.Height,
            0x0004 | 0x0010 | 0x0040); // SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW
        Activate();
        Focus();
        Keyboard.Focus(this);
        _selectionTimeoutTimer.Start();
    }

    public void CancelSelection()
    {
        if (!IsVisible)
            return;
        _isDragging = false;
        ReleaseMouseCapture();
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetCursorPosition(out var point) || !_monitorRegion.Contains(point))
            return;

        _dragStart = point;
        _dragCurrent = point;
        _isDragging = true;
        HintText.Text = DefaultHint;
        CaptureMouse();
        UpdateSelectionVisual();
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || !TryGetCursorPosition(out var point))
            return;

        _dragCurrent = point;
        UpdateSelectionVisual();
        e.Handled = true;
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;

        if (TryGetCursorPosition(out var point))
            _dragCurrent = point;
        _isDragging = false;
        ReleaseMouseCapture();

        var decision = ScreenshotSelectionModel.Evaluate(
            _dragStart,
            _dragCurrent,
            _monitorRegion,
            resourceLimits: _resourceLimits);
        if (!decision.IsAccepted)
        {
            HintText.Text = decision.Message;
            SelectionLabel.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        _completed = true;
        Hide();
        Close();
        SelectionCompleted?.Invoke(decision.Region);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelSelection();
            e.Handled = true;
        }
    }

    private void UpdateSelectionVisual()
    {
        var displayCurrent = ClampToMonitor(_dragCurrent);
        var decision = ScreenshotSelectionModel.Evaluate(
            _dragStart,
            displayCurrent,
            _monitorRegion,
            minimumDimension: 1,
            resourceLimits: _resourceLimits);
        if (!decision.IsAccepted)
        {
            var left = Math.Min(_dragStart.X, displayCurrent.X);
            var top = Math.Min(_dragStart.Y, displayCurrent.Y);
            var right = Math.Max(_dragStart.X, displayCurrent.X);
            var bottom = Math.Max(_dragStart.Y, displayCurrent.Y);
            DrawSelection(new ScreenshotRegion(left, top, right - left, bottom - top));
            SelectionLabel.Visibility = Visibility.Collapsed;
            return;
        }

        DrawSelection(decision.Region);
        SelectionLabelText.Text = $"{decision.Region.Width} × {decision.Region.Height}";
        SelectionLabel.Visibility = Visibility.Visible;
    }

    private void DrawSelection(ScreenshotRegion region)
    {
        var left = (region.Left - _monitorRegion.Left) / _dpiScale.X;
        var top = (region.Top - _monitorRegion.Top) / _dpiScale.Y;
        var width = region.Width / _dpiScale.X;
        var height = region.Height / _dpiScale.Y;
        Canvas.SetLeft(SelectionBorder, left);
        Canvas.SetTop(SelectionBorder, top);
        SelectionBorder.Width = Math.Max(width, 1);
        SelectionBorder.Height = Math.Max(height, 1);
        SelectionBorder.Visibility = Visibility.Visible;

        Canvas.SetLeft(SelectionLabel, Math.Max(0, left));
        Canvas.SetTop(SelectionLabel, Math.Max(0, top - 28));
    }

    private PhysicalPoint ClampToMonitor(PhysicalPoint point) =>
        new(
            Math.Clamp(point.X, _monitorRegion.Left, _monitorRegion.Right - 1),
            Math.Clamp(point.Y, _monitorRegion.Top, _monitorRegion.Bottom - 1));

    private static bool TryGetCursorPosition(out PhysicalPoint point)
    {
        if (Win32Api.GetCursorPos(out var cursor))
        {
            point = new PhysicalPoint(cursor.X, cursor.Y);
            return true;
        }

        point = default;
        return false;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _selectionTimeoutTimer.Stop();
        ReleaseMouseCapture();
        if (!_completed)
            Cancelled?.Invoke();
    }
}
