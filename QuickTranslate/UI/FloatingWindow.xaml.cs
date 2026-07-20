using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

            // ★ 根据锚点所在显示器的工作区动态限制 ScrollViewer 最大高度
            // SizeToContent="WidthAndHeight" 让 WPF 自动处理窗口尺寸
            // 只需约束 ScrollViewer.MaxHeight 防止窗口超出屏幕
            var workArea = Win32Api.GetWorkAreaAtPoint(anchorPosition);
            // Border chrome: Margin(4*2=8) + Padding(10*2=20) = 28px
            double chromeHeight = 28;
            double maxWindowH = (workArea.Bottom - workArea.Top) - 80;
            double scrollerMaxH = maxWindowH - chromeHeight;
            TranslationScroller.MaxHeight = Math.Max(scrollerMaxH, 80);

            // 先 Show 以获取 ActualWidth/ActualHeight，再精确定位
            Show();
            UpdateLayout();
            PositionBelowAnchor(anchorPosition);
            Activate();
            ResetAutoHideTimer();
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
            ClampToWorkArea();

            ResetAutoHideTimer();
        }

        /// <summary>
        /// 在锚点正下方水平居中定位窗口（锚点通常为红点中心）
        /// 支持6屏异现及屏幕边缘避让
        /// </summary>
        private void PositionBelowAnchor(Point anchorCenter)
        {
            double gap = 8;  // 锚点与悬浮窗间距
            double w = ActualWidth;
            double h = ActualHeight;

            // 水平居中于锚点下方
            double left = anchorCenter.X - w / 2;
            double top = anchorCenter.Y + gap;

            // 获取锚点所在显示器的工作区
            var workArea = Win32Api.GetWorkAreaAtPoint(anchorCenter);

            // 右边界避让
            if (left + w > workArea.Right)
                left = workArea.Right - w;
            // 左边界避让
            if (left < workArea.Left)
                left = workArea.Left;
            // 底部边界：不够空间则显示在锚点上方
            if (top + h > workArea.Bottom)
                top = anchorCenter.Y - gap - h;
            // 顶部边界
            if (top < workArea.Top)
                top = workArea.Top;

            Left = left;
            Top = top;
        }

        /// <summary>
        /// 检查并约束窗口在工作区内（流式输出导致窗口增长后调用）
        /// </summary>
        private void ClampToWorkArea()
        {
            if (_anchorPosition == default) return;

            var workArea = Win32Api.GetWorkAreaAtPoint(_anchorPosition);
            double w = ActualWidth;
            double h = ActualHeight;
            double newLeft = Left;
            double newTop = Top;
            bool needMove = false;

            // 底部超出：上移窗口
            if (newTop + h > workArea.Bottom)
            {
                newTop = workArea.Bottom - h;
                needMove = true;
            }
            // 顶部超出：下移窗口
            if (newTop < workArea.Top)
            {
                newTop = workArea.Top;
                needMove = true;
            }
            // 右侧超出：左移窗口
            if (newLeft + w > workArea.Right)
            {
                newLeft = workArea.Right - w;
                needMove = true;
            }
            // 左侧超出：右移窗口
            if (newLeft < workArea.Left)
            {
                newLeft = workArea.Left;
                needMove = true;
            }

            if (needMove)
            {
                Left = newLeft;
                Top = newTop;
            }
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
