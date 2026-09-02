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
    private readonly OverlayLayoutEngine _layoutEngine;
    private readonly Dictionary<string, ScreenshotTranslationUnit> _unitsById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedUnitIds = new(StringComparer.Ordinal);
    private readonly bool _incremental;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;

    private const double RenderSafetyPixels = 2;

    public ScreenshotOverlayLayoutResult LayoutResult { get; private set; } =
        new(Array.Empty<ScreenshotOverlayLayout>());

    public bool HasRenderableItems => _incremental
        ? _unitsById.Count > 0
        : LayoutResult.Items.Any(static item => item.Status != ScreenshotOverlayLayoutStatus.Skipped);

    public int ExpectedCount => _incremental
        ? _unitsById.Count
        : LayoutResult.Items.Count(item => item.Status != ScreenshotOverlayLayoutStatus.Skipped);

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
        _pixelWidth = image.PixelWidth;
        _pixelHeight = image.PixelHeight;
        _incremental = false;
        InitializeComponent();
        _layoutEngine = new OverlayLayoutEngine(measure: MeasureTextWithWpf);
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
        _pixelWidth = image.PixelWidth;
        _pixelHeight = image.PixelHeight;
        _incremental = true;
        InitializeComponent();
        _layoutEngine = new OverlayLayoutEngine(measure: MeasureTextWithWpf);
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
        var output = layout.Items.ToArray();
        for (var index = 0; index < layout.Items.Count; index++)
        {
            var item = layout.Items[index];
            if (item.Status == ScreenshotOverlayLayoutStatus.Skipped)
                continue;

            var border = CreateCard(item, pending ? string.Empty : item.Translation, pending);
            if (!pending && !VerifyRenderedCard(border, item))
            {
                output[index] = ToSkipped(item, "wpf_measurement_failed");
                continue;
            }

            if (!pending)
            {
                AddCard(border);
            }
        }

        return new ScreenshotOverlayLayoutResult(output);
    }

    /// <summary>替换单个已完成单元，不改变任何既有卡片的位置。</summary>
    public bool TryUpdateTranslation(TranslatedTextUnit translated)
    {
        ArgumentNullException.ThrowIfNull(translated);
        if (!_incremental || string.IsNullOrWhiteSpace(translated.UnitId) ||
            string.IsNullOrWhiteSpace(translated.Translation) ||
            !_unitsById.TryGetValue(translated.UnitId, out var sourceUnit) ||
            _completedUnitIds.Contains(translated.UnitId))
        {
            return false;
        }

        var occupied = LayoutResult.Items
            .Where(item => _completedUnitIds.Contains(item.UnitId) &&
                           item.Status != ScreenshotOverlayLayoutStatus.Skipped)
            .Select(static item => item.LayoutBounds)
            .ToArray();
        var overlayItem = new ScreenshotOverlayItem(
            sourceUnit.Bounds,
            translated.Translation.Trim(),
            sourceUnit.Blocks.Count == 1 ? sourceUnit.Blocks[0].Polygon : null,
            sourceUnit.UnitId,
            AverageConfidence(sourceUnit.Blocks));
        var layout = _layoutEngine.LayoutIncremental(
            _pixelWidth,
            _pixelHeight,
            overlayItem,
            occupied);
        if (layout.Status == ScreenshotOverlayLayoutStatus.Skipped)
            return false;

        var border = CreateCard(layout, translated.Translation.Trim(), pending: false);
        if (!VerifyRenderedCard(border, layout))
            return false;

        AddCard(border);
        _completedUnitIds.Add(translated.UnitId);
        var existingIndex = LayoutResult.Items
            .Select((item, index) => (item, index))
            .FirstOrDefault(pair => string.Equals(pair.item.UnitId, translated.UnitId, StringComparison.Ordinal))
            .index;
        var updated = LayoutResult.Items.ToArray();
        if (existingIndex >= 0 && existingIndex < updated.Length)
            updated[existingIndex] = layout;
        LayoutResult = new ScreenshotOverlayLayoutResult(updated);
        return true;
    }

    private Border CreateCard(
        ScreenshotOverlayLayout item,
        string text,
        bool pending)
    {
        var x = item.LayoutBounds.X / _dpiScale.X;
        var y = item.LayoutBounds.Y / _dpiScale.Y;
        var width = item.LayoutBounds.Width / _dpiScale.X;
        var height = item.LayoutBounds.Height / _dpiScale.Y;
        var isDegraded = item.Status == ScreenshotOverlayLayoutStatus.Degraded;
        var borderDip = isDegraded ? 1.5 : 1;
        var horizontalPadding = Math.Max(
            0,
            (4 - borderDip * _dpiScale.X / 2) / _dpiScale.X);
        var verticalPadding = Math.Max(
            0,
            (3 - borderDip * _dpiScale.Y / 2) / _dpiScale.Y);
        var textBlock = new TextBlock
        {
            Text = text,
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
            Visibility = pending ? Visibility.Hidden : Visibility.Visible,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(border, Math.Max(0, x));
        Canvas.SetTop(border, Math.Max(0, y));
        return border;
    }

    private void AddCard(Border border)
    {
        OverlayCanvas.Children.Add(border);
    }

    private bool VerifyRenderedCard(Border border, ScreenshotOverlayLayout item)
    {
        var widthDip = item.LayoutBounds.Width / _dpiScale.X;
        var heightDip = item.LayoutBounds.Height / _dpiScale.Y;
        border.Measure(new Size(widthDip, heightDip));
        border.Arrange(new Rect(0, 0, widthDip, heightDip));
        var textBlock = (TextBlock)border.Child;
        var contentWidth = Math.Max(
            0,
            widthDip - border.Padding.Left - border.Padding.Right -
            border.BorderThickness.Left - border.BorderThickness.Right);
        var contentHeight = Math.Max(
            0,
            heightDip - border.Padding.Top - border.Padding.Bottom -
            border.BorderThickness.Top - border.BorderThickness.Bottom);
        textBlock.Measure(new Size(contentWidth, double.PositiveInfinity));
        return textBlock.DesiredSize.Width <= contentWidth + 0.01 &&
               textBlock.DesiredSize.Height <= contentHeight + 0.01;
    }

    private OverlayTextMeasurement MeasureTextWithWpf(
        string text,
        double fontSize,
        int candidateWidth)
    {
        const double borderDip = 1.5;
        var horizontalPadding = Math.Max(
            0,
            (4 - borderDip * _dpiScale.X / 2) / _dpiScale.X);
        var verticalPadding = Math.Max(
            0,
            (3 - borderDip * _dpiScale.Y / 2) / _dpiScale.Y);
        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = Math.Max(1, fontSize / _dpiScale.Y),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None
        };
        textBlock.SetValue(TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);
        var contentWidth = Math.Max(
            1,
            candidateWidth / _dpiScale.X -
            horizontalPadding * 2 - borderDip * 2 / _dpiScale.X -
            RenderSafetyPixels / _dpiScale.X);
        textBlock.Measure(new Size(contentWidth, double.PositiveInfinity));
        var width = (textBlock.DesiredSize.Width + horizontalPadding * 2 + borderDip * 2) * _dpiScale.X +
                    RenderSafetyPixels;
        var height = (textBlock.DesiredSize.Height + verticalPadding * 2 + borderDip * 2) * _dpiScale.Y +
                     RenderSafetyPixels;
        var lineCount = Math.Max(1, (int)Math.Ceiling(textBlock.DesiredSize.Height / Math.Max(1, fontSize / _dpiScale.Y * 1.35)));
        return new OverlayTextMeasurement(width, height, lineCount);
    }

    private static ScreenshotOverlayLayout ToSkipped(
        ScreenshotOverlayLayout item,
        string reason) => item with
        {
            LayoutBounds = default,
            FontSize = 0,
            LineCount = 0,
            MeasuredTextWidth = 0,
            MeasuredTextHeight = 0,
            Status = ScreenshotOverlayLayoutStatus.Skipped,
            DegradationReason = reason
        };

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
