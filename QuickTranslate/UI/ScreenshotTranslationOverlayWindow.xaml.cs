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
        BuildOverlay(items, image.PixelWidth, image.PixelHeight);
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

    private void BuildOverlay(
        IReadOnlyList<ScreenshotOverlayItem> items,
        int pixelWidth,
        int pixelHeight)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Translation) ||
                !item.Bounds.IsWithin(pixelWidth, pixelHeight))
                continue;

            var x = item.Bounds.X / _dpiScale.X;
            var y = item.Bounds.Y / _dpiScale.Y;
            var width = item.Bounds.Width / _dpiScale.X;
            var height = item.Bounds.Height / _dpiScale.Y;
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(218, 15, 23, 42)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
                CornerRadius = new CornerRadius(2),
                Width = Math.Max(width, 24),
                MinHeight = Math.Max(height, 20),
                MaxWidth = Math.Max(width, 24),
                MaxHeight = Math.Max(height, 20),
                Child = new TextBlock
                {
                    Text = item.Translation.Trim(),
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    FontSize = Math.Clamp(height * 0.62, 10, 28),
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left
                }
            };
            border.Child.SetValue(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);
            Canvas.SetLeft(border, Math.Max(0, x));
            Canvas.SetTop(border, Math.Max(0, y));
            OverlayCanvas.Children.Add(border);
        }
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
