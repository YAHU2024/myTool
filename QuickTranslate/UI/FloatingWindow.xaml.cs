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
        public void ShowTranslation(string source, string translation, Point cursorPosition)
        {
            SourceTextBlock.Text = source;
            TranslationTextBlock.Text = translation;

            // 定位到鼠标右下方
            PositionNearCursor(cursorPosition);

            Show();
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
        /// 设置原文
        /// </summary>
        public void SetSource(string source)
        {
            SourceTextBlock.Text = source;
        }

        /// <summary>
        /// 在鼠标附近定位窗口
        /// </summary>
        private void PositionNearCursor(Point cursorPosition)
        {
            double offsetX = 15;
            double offsetY = 15;

            var screen = SystemParameters.WorkArea;

            // 先估算窗口大小
            double estimatedWidth = 300;
            double estimatedHeight = 100;

            double left = cursorPosition.X + offsetX;
            double top = cursorPosition.Y + offsetY;

            // 避免超出屏幕右侧
            if (left + estimatedWidth > screen.Right)
            {
                left = cursorPosition.X - estimatedWidth - offsetX;
            }

            // 避免超出屏幕底部
            if (top + estimatedHeight > screen.Bottom)
            {
                top = cursorPosition.Y - estimatedHeight - offsetY;
            }

            // 确保不超出左边界和上边界
            if (left < screen.Left) left = screen.Left;
            if (top < screen.Top) top = screen.Top;

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
