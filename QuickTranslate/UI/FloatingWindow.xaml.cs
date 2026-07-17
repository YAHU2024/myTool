using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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
        /// 显示翻译结果
        /// </summary>
        public void ShowTranslation(string translation, Point anchorPosition)
        {
            TranslationTextBlock.Text = translation;

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
    }
}
