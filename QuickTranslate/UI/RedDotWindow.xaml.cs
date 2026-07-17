using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using QuickTranslate.Core;

namespace QuickTranslate.UI
{
    /// <summary>
    /// 红色小圆点窗口 - 显示在选中文本附近，鼠标悬停触发翻译
    /// </summary>
    public partial class RedDotWindow : Window
    {
        private readonly DispatcherTimer _autoHideTimer;

        /// <summary>
        /// 红点在屏幕上的中心位置（用于悬浮窗定位）
        /// </summary>
        public System.Windows.Point DotScreenPosition { get; private set; }

        /// <summary>
        /// 鼠标悬停时触发
        /// </summary>
        public event Action? HoverTriggered;

        /// <summary>
        /// 红点被取消时触发（自动隐藏等）
        /// </summary>
        public event Action? Cancelled;

        public RedDotWindow()
        {
            InitializeComponent();

            // 自动隐藏计时器（8秒无操作后隐藏）
            _autoHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(8)
            };
            _autoHideTimer.Tick += (s, e) =>
            {
                Hide();
                _autoHideTimer.Stop();
                Cancelled?.Invoke();
            };

            // 鼠标离开时开始计时
            MouseLeave += (s, e) =>
            {
                ResetAutoHideTimer();
            };
        }

        /// <summary>
        /// 根据选中文本位置显示红点
        /// UIA 成功时红点中心对齐选中文本末端坐标；降级时使用估算坐标
        /// </summary>
        public void ShowAt(SelectionLocation location)
        {
            var anchor = location.IsValid ? location.EndPoint : location.FallbackPoint;

            // 红点窗口 16x16，中心偏移 = 8
            Left = anchor.X - 8;
            Top = anchor.Y - 8;

            // 记录红点中心点坐标（用于悬浮窗定位）
            DotScreenPosition = new System.Windows.Point(Left + 8, Top + 8);

            Show();
            ResetAutoHideTimer();
        }

        /// <summary>
        /// 隐藏红点
        /// </summary>
        public new void Hide()
        {
            _autoHideTimer.Stop();
            base.Hide();
        }

        /// <summary>
        /// 鼠标进入红点时触发翻译
        /// </summary>
        private void Grid_MouseEnter(object sender, MouseEventArgs e)
        {
            _autoHideTimer.Stop();
            HoverTriggered?.Invoke();
        }

        /// <summary>
        /// 重置自动隐藏计时器
        /// </summary>
        private void ResetAutoHideTimer()
        {
            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }
    }
}
