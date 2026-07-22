using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using QuickTranslate.Core;
using QuickTranslate.Helpers;

namespace QuickTranslate.UI
{
    /// <summary>
    /// 悬浮翻译窗口 - 显示在鼠标旁的翻译结果
    /// </summary>
    public partial class FloatingWindow : Window
    {
        private readonly DispatcherTimer _autoHideTimer;
        private bool _isMouseInside;
        private Point _anchorPosition; // 记录锚点，用于流式输出时重新定位
        private string? _sourceText; // 存储原文，供解析使用
        private bool _placeAbove;

        /// <summary>
        /// 用户点击[解析]标签时触发，携带原文
        /// </summary>
        public event Action<string>? AnalysisRequested;

        public FloatingWindow()
        {
            InitializeComponent();

            // 初始化自动隐藏计时器（5秒）
            _autoHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _autoHideTimer.Tick += (s, e) =>
            {
                if (!_isMouseInside)
                {
                    Hide();
                }
                _autoHideTimer.Stop();
            };

            // 鼠标进入时重置计时器
            MouseEnter += (s, e) =>
            {
                _isMouseInside = true;
                ResetAutoHideTimer();
            };

            // 鼠标离开时重新启动计时器
            MouseLeave += (s, e) =>
            {
                _isMouseInside = false;
                ResetAutoHideTimer();
            };

            // 失去焦点时隐藏
            Deactivated += (s, e) =>
            {
                if (!_isMouseInside)
                {
                    Hide();
                }
            };
        }

        /// <summary>
        /// 显示内容类型标签（翻译/命令解析/术语解释）
        /// </summary>
        public void ShowContentTypeLabel(ContentType contentType)
        {
            switch (contentType)
            {
                case ContentType.Code:
                    ContentTypeLabel.Text = "[命令解析]";
                    ContentTypeLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00)); // 橙色
                    ContentTypeLabel.Visibility = Visibility.Visible;
                    ContentTypeLabel.ToolTip = null;
                    break;
                case ContentType.Term:
                    ContentTypeLabel.Text = "[术语解释]";
                    ContentTypeLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)); // 青绿
                    ContentTypeLabel.Visibility = Visibility.Visible;
                    ContentTypeLabel.ToolTip = null;
                    break;
                case ContentType.Analysis:
                    ContentTypeLabel.Text = "[深度解析]";
                    ContentTypeLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)); // 紫色
                    ContentTypeLabel.Visibility = Visibility.Visible;
                    ContentTypeLabel.ToolTip = null;
                    break;
                default:
                    ContentTypeLabel.Visibility = Visibility.Collapsed;
                    ContentTypeLabel.ToolTip = null;
                    break;
            }
        }

        /// <summary>
        /// 显示可点击的[解析]标签（兆底场景）
        /// </summary>
        public void ShowAnalysisTag(string sourceText)
        {
            _sourceText = sourceText;
            ContentTypeLabel.Text = "[解析]";
            ContentTypeLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)); // 紫色
            ContentTypeLabel.Visibility = Visibility.Visible;
            ContentTypeLabel.ToolTip = "点击用目标语言深度解析此文本";
        }

        /// <summary>
        /// 显示翻译结果
        /// </summary>
        public void ShowTranslation(string translation, Point anchorPosition)
        {
            TranslationTextBlock.Text = translation;
            _anchorPosition = anchorPosition;
            _sourceText = null; // 重置原文
            ContentTypeLabel.ToolTip = null; // 重置提示
            ContentTypeLabel.Visibility = Visibility.Collapsed;

            // UIA and mouse coordinates are physical screen pixels. Keep the same
            // contract through layout and use the target monitor's actual DPI.
            var workArea = Win32Api.GetPhysicalWorkAreaAtPoint(anchorPosition);
            var scale = DpiHelper.GetScaleForPhysicalPoint(anchorPosition);
            const double chromeHeightDip = 28;
            const double gapDip = 8;
            var gap = gapDip * scale.Y;
            _placeAbove = FloatingWindowPlacement.ShouldPlaceAbove(anchorPosition, workArea, gap);
            var availableHeight = _placeAbove
                ? anchorPosition.Y - gap - workArea.Top
                : workArea.Bottom - anchorPosition.Y - gap;
            TranslationScroller.MaxHeight = Math.Max(
                availableHeight / scale.Y - chromeHeightDip,
                80);

            // A reused window must not paint its old position while its content and
            // native position are being updated.
            Opacity = 0;
            Show();
            UpdateLayout();
            PositionWindowAtAnchor();
            Opacity = 1;
            ResetAutoHideTimer();
        }

        /// <summary>
        /// Re-measures and repositions after a content-type label changes size.
        /// </summary>
        public void RefreshPlacement()
        {
            if (!IsVisible)
                return;

            UpdateLayout();
            PositionWindowAtAnchor();
        }

        /// <summary>
        /// 更新译文（用于流式输出）
        /// </summary>
        public void UpdateTranslation(string translation)
        {
            TranslationTextBlock.Text = translation;

            // ★ 流式输出时自动滚动到底部，确保最新译文可见
            if (TranslationScroller != null)
            {
                TranslationScroller.ScrollToEnd();
            }

            // ★ 内容增长后重新布局，然后检查边界
            UpdateLayout();
            PositionWindowAtAnchor();

            ResetAutoHideTimer();
        }

        /// <summary>
        /// 使用物理屏幕坐标将窗口固定在锚点上方或下方，并约束在目标工作区内。
        /// </summary>
        private void PositionWindowAtAnchor()
        {
            if (_anchorPosition == default || ActualWidth <= 0 || ActualHeight <= 0)
                return;

            var workArea = Win32Api.GetPhysicalWorkAreaAtPoint(_anchorPosition);
            if (workArea.IsEmpty)
                return;

            var physicalSize = DpiHelper.LogicalSizeToPhysical(
                new Size(ActualWidth, ActualHeight),
                _anchorPosition);
            var scale = DpiHelper.GetScaleForPhysicalPoint(_anchorPosition);
            var gap = 8 * scale.Y;
            var rect = FloatingWindowPlacement.Calculate(
                _anchorPosition,
                physicalSize,
                workArea,
                _placeAbove,
                gap);

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            Win32Api.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                (int)Math.Round(rect.Left),
                (int)Math.Round(rect.Top),
                (int)Math.Round(rect.Width),
                (int)Math.Round(rect.Height),
                0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
        }

        /// <summary>
        /// 重置自动隐藏计时器
        /// </summary>
        private void ResetAutoHideTimer()
        {
            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }

        /// <summary>
        /// 点击译文复制到剪贴板
        /// </summary>
        private void TranslationTextBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var text = TranslationTextBlock.Text;
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    Clipboard.SetText(text);
                    // 短暂显示提示
                    var originalForeground = TranslationTextBlock.Foreground;
                    TranslationTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    timer.Tick += (s, args) =>
                    {
                        TranslationTextBlock.Foreground = originalForeground;
                        timer.Stop();
                    };
                    timer.Start();
                }
                catch
                {
                    // 忽略剪贴板异常
                }
            }
        }

        /// <summary>
        /// 点击[解析]标签触发深度解析
        /// </summary>
        private void ContentTypeLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ContentTypeLabel.Text == "[解析]" && _sourceText != null)
            {
                AnalysisRequested?.Invoke(_sourceText);
            }
        }
    }
}
