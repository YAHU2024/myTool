using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Input;
using QuickTranslate.Core;
using QuickTranslate.Helpers;
using QuickTranslate.Models;

namespace QuickTranslate.UI;

/// <summary>
/// 在截图原图上覆盖译文。背景和文字只保存在当前窗口内，关闭后不写入历史。
/// 复杂多边形目前使用其轴对齐包围盒做遮罩，避免错误擦除原图；Polygon 保留给后续
/// 精确背景修复阶段。
/// </summary>
public partial class ScreenshotTranslationOverlayWindow : Window
{
    private readonly ScreenshotRegion _region;
    private readonly Point _dpiScale;

    public ScreenshotOverlayLayoutResult LayoutResult { get; private set; } =
        new(Array.Empty<ScreenshotOverlayLayout>());

    public bool HasRenderableItems => LayoutResult.Items.Any(static item =>
        item.Status != ScreenshotOverlayLayoutStatus.Skipped);

    public ScreenshotTranslationOverlayWindow(
        ScreenshotRegion region,
        OcrImage image,
        IReadOnlyList<ScreenshotOverlayItem> items)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(items);
        if (!region.IsValid)
            throw new ArgumentException("截图区域无效。", nameof(region));
        image.Validate();

        _region = region;
        _dpiScale = DpiHelper.GetScaleForPhysicalPoint(new Point(region.Left, region.Top));
        InitializeComponent();
        ConfigureWindow(image);
        LayoutResult = BuildOverlay(items, image.PixelWidth, image.PixelHeight);
    }

    public void ShowOverlay()
    {
        Show();
        UpdateLayout();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Win32Api.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            _region.Left,
            _region.Top,
            _region.Width,
            _region.Height,
            0x0004 | 0x0010 | 0x0040); // SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    private void ConfigureWindow(OcrImage image)
    {
        Left = _region.Left / _dpiScale.X;
        Top = _region.Top / _dpiScale.Y;
        Width = _region.Width / _dpiScale.X;
        Height = _region.Height / _dpiScale.Y;

        ScreenshotImage.Source = BitmapSource.Create(
            image.PixelWidth,
            image.PixelHeight,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            image.BgraPixels.ToArray(),
            image.Stride);
        ScreenshotImage.Source.Freeze();
    }

    private ScreenshotOverlayLayoutResult BuildOverlay(
        IReadOnlyList<ScreenshotOverlayItem> items,
        int pixelWidth,
        int pixelHeight)
    {
        var layout = new OverlayLayoutEngine().Layout(pixelWidth, pixelHeight, items);
        foreach (var item in layout.Items)
        {
            if (item.Status == ScreenshotOverlayLayoutStatus.Skipped)
                continue;

            var x = item.LayoutBounds.X / _dpiScale.X;
            var y = item.LayoutBounds.Y / _dpiScale.Y;
            var width = item.LayoutBounds.Width / _dpiScale.X;
            var height = item.LayoutBounds.Height / _dpiScale.Y;
            var isDegraded = item.Status == ScreenshotOverlayLayoutStatus.Degraded;
            var borderDip = isDegraded ? 1.5 : 1;
            // The logical engine includes 8 px horizontal and 6 px vertical
            // card insets. Reserve the border inside that budget so WPF's
            // content presenter receives the same physical text area.
            var horizontalPadding = Math.Max(
                0,
                (4 - borderDip * _dpiScale.X / 2) / _dpiScale.X);
            var verticalPadding = Math.Max(
                0,
                (3 - borderDip * _dpiScale.Y / 2) / _dpiScale.Y);
            var border = new Border
            {
                Background = new SolidColorBrush(isDegraded
                    ? Color.FromArgb(228, 69, 43, 15)
                    : Color.FromArgb(218, 15, 23, 42)),
                BorderBrush = new SolidColorBrush(isDegraded
                    ? Color.FromArgb(235, 251, 191, 36)
                    : Color.FromArgb(220, 255, 255, 255)),
                BorderThickness = new Thickness(borderDip),
                Padding = new Thickness(
                    horizontalPadding,
                    verticalPadding,
                    horizontalPadding,
                    verticalPadding),
                CornerRadius = new CornerRadius(2),
                Width = Math.Max(1, width),
                Height = Math.Max(1, height),
                ToolTip = isDegraded ? "译文布局已降级，已保持全文显示" : null,
                Child = new TextBlock
                {
                    Text = item.Translation,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    FontSize = Math.Max(1, item.FontSize / _dpiScale.Y),
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.None,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left
                }
            };
            border.Child.SetValue(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);
            Canvas.SetLeft(border, Math.Max(0, x));
            Canvas.SetTop(border, Math.Max(0, y));
            OverlayCanvas.Children.Add(border);
        }

        return layout;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        Close();
        e.Handled = true;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Close();
        e.Handled = true;
    }
}
