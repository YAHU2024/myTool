using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Input;
using QuickTranslate.Core;
using QuickTranslate.Helpers;
using QuickTranslate.Models;
using QuickTranslate.Services;

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
    private readonly OverlayLayoutEngine _layoutEngine = new();
    private readonly Dictionary<string, Border> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScreenshotTranslationUnit> _unitsById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedUnitIds = new(StringComparer.Ordinal);
    private readonly bool _incremental;

    public ScreenshotOverlayLayoutResult LayoutResult { get; private set; } =
        new(Array.Empty<ScreenshotOverlayLayout>());

    public bool HasRenderableItems => LayoutResult.Items.Any(static item =>
        item.Status != ScreenshotOverlayLayoutStatus.Skipped);

    public int ExpectedCount => LayoutResult.Items.Count(item =>
        item.Status != ScreenshotOverlayLayoutStatus.Skipped);

    public int CompletedCount => _completedUnitIds.Count;

    public event Action<IReadOnlyList<ScreenshotTranslationUnit>>? RetryRequested;

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
        _incremental = false;
        InitializeComponent();
        ConfigureWindow(image);
        LayoutResult = BuildOverlay(items, image.PixelWidth, image.PixelHeight, pending: false);
    }

    /// <summary>
    /// 创建一个尚未显示译文的稳定布局。布局只计算一次，后续单元完成时
    /// 只替换对应卡片文本，避免每个 SSE 结果到达都触发全量碰撞重排。
    /// </summary>
    public ScreenshotTranslationOverlayWindow(
        ScreenshotRegion region,
        OcrImage image,
        IReadOnlyList<ScreenshotTranslationUnit> units)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(units);
        if (!region.IsValid)
            throw new ArgumentException("截图区域无效。", nameof(region));
        image.Validate();

        _region = region;
        _dpiScale = DpiHelper.GetScaleForPhysicalPoint(new Point(region.Left, region.Top));
        _incremental = true;
        InitializeComponent();
        ConfigureWindow(image);
        var seeds = units.Select(static unit => new ScreenshotOverlayItem(
            unit.Bounds,
            unit.SourceText,
            unit.Blocks.Count == 1 ? unit.Blocks[0].Polygon : null,
            unit.UnitId,
            AverageConfidence(unit.Blocks))).ToArray();
        foreach (var unit in units)
            _unitsById[unit.UnitId] = unit;
        LayoutResult = BuildOverlay(seeds, image.PixelWidth, image.PixelHeight, pending: true);
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
        int pixelHeight,
        bool pending)
    {
        var layout = _layoutEngine.Layout(pixelWidth, pixelHeight, items);
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
            var textBlock = new TextBlock
            {
                Text = pending ? string.Empty : item.Translation,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = Math.Max(1, item.FontSize / _dpiScale.Y),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            textBlock.SetValue(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);
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
                Child = textBlock,
                Visibility = pending ? Visibility.Hidden : Visibility.Visible
            };
            Canvas.SetLeft(border, Math.Max(0, x));
            Canvas.SetTop(border, Math.Max(0, y));
            OverlayCanvas.Children.Add(border);
            _cards[item.UnitId] = border;
        }

        return layout;
    }

    /// <summary>替换单个已完成单元，不改变任何既有卡片的位置。</summary>
    public bool TryUpdateTranslation(TranslatedTextUnit translated)
    {
        ArgumentNullException.ThrowIfNull(translated);
        if (!_incremental || string.IsNullOrWhiteSpace(translated.UnitId) ||
            string.IsNullOrWhiteSpace(translated.Translation) ||
            !_cards.TryGetValue(translated.UnitId, out var border))
        {
            return false;
        }

        var layout = LayoutResult.Items.FirstOrDefault(item =>
            string.Equals(item.UnitId, translated.UnitId, StringComparison.Ordinal));
        if (layout is null)
            return false;
        if (!_completedUnitIds.Add(translated.UnitId))
            return false;

        var textBlock = (TextBlock)border.Child;
        textBlock.Text = translated.Translation.Trim();
        textBlock.FontSize = Math.Max(
            1,
            _layoutEngine.FitFontSize(
                textBlock.Text,
                layout.LayoutBounds,
                layout.FontSize) / _dpiScale.Y);
        border.Visibility = Visibility.Visible;
        return true;
    }

    public void MarkPartial(string message, bool canRetry = false)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        StatusText.Text = message.Trim();
        StatusText.Visibility = Visibility.Visible;
        RetryButton.Visibility = canRetry && GetMissingUnits().Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public IReadOnlyList<ScreenshotTranslationUnit> GetMissingUnits() =>
        _unitsById.Values
            .Where(unit => !_completedUnitIds.Contains(unit.UnitId))
            .ToArray();

    public void ClearPartial()
    {
        StatusText.ClearValue(TextBlock.TextProperty);
        StatusText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        RetryRequested?.Invoke(GetMissingUnits());
        e.Handled = true;
    }

    private void RetryButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private static double? AverageConfidence(IReadOnlyList<OcrTextBlock> blocks)
    {
        var values = blocks
            .Where(static block => block.Confidence is { } confidence && double.IsFinite(confidence))
            .Select(static block => block.Confidence!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Average();
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
