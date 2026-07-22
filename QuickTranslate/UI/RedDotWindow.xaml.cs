using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;
using QuickTranslate.Core;
using QuickTranslate.Helpers;

namespace QuickTranslate.UI
{
    /// <summary>
    /// 红色小圆点窗口 - 显示在选中文本附近，鼠标悬停触发翻译
    /// </summary>
    public partial class RedDotWindow : Window
    {
        private readonly DispatcherTimer _autoHideTimer;
        // Prevents the mouse-up position from immediately activating the dot.
        // It becomes armed only after the pointer has actually left the dot.
        private bool _hoverArmed;

        /// <summary>
        /// 红点在屏幕上的物理像素中心位置（用于悬浮窗定位）
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
                if (IsVisible)
                    _hoverArmed = true;
                ResetAutoHideTimer();
            };
        }

        /// <summary>
        /// 根据选中文本位置显示红点
        /// UIA 成功时红点中心对齐选中文本末端坐标；降级时使用估算坐标
        /// </summary>
        public void ShowAt(SelectionLocation location)
        {
            // Every new dot starts disarmed. If the pointer is already over the
            // fallback position, the first MouseEnter is intentionally ignored.
            _hoverArmed = false;
            var physicalAnchor = location.IsValid ? location.EndPoint : location.FallbackPoint;
            var anchor = DpiHelper.PhysicalToLogical(physicalAnchor);

            // 红点窗口 16x16，中心偏移 = 8
            Left = anchor.X - 8;
            Top = anchor.Y - 8;

            // Keep the public anchor in physical pixels. FloatingWindow uses the
            // same coordinate contract and positions its HWND natively.
            DotScreenPosition = physicalAnchor;

            Show();
            // WPF window coordinates are DIP; native placement below is performed in physical pixels.
            UpdateLayout();
            var hwnd = new WindowInteropHelper(this).Handle;
            var physicalSize = DpiHelper.LogicalSizeToPhysical(new System.Windows.Size(ActualWidth, ActualHeight), physicalAnchor);
            var physicalWorkArea = Win32Api.GetPhysicalWorkAreaAtPoint(physicalAnchor);
            var px = physicalAnchor.X - physicalSize.Width / 2;
            var py = physicalAnchor.Y - physicalSize.Height / 2;
            if (!physicalWorkArea.IsEmpty)
            {
                px = Math.Max(physicalWorkArea.Left, Math.Min(px, physicalWorkArea.Right - physicalSize.Width));
                py = Math.Max(physicalWorkArea.Top, Math.Min(py, physicalWorkArea.Bottom - physicalSize.Height));
            }
            Win32Api.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(px), (int)Math.Round(py),
                (int)Math.Round(physicalSize.Width), (int)Math.Round(physicalSize.Height),
                0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
            Left = px / DpiHelper.GetScaleForPhysicalPoint(physicalAnchor).X;
            Top = py / DpiHelper.GetScaleForPhysicalPoint(physicalAnchor).Y;
            DotScreenPosition = new System.Windows.Point(
                px + physicalSize.Width / 2,
                py + physicalSize.Height / 2);
            // If the pointer is already outside the newly shown window, the
            // next enter is an intentional hover and may activate normally.
            // Only suppress the enter when the window appeared under the pointer.
            if (Win32Api.GetCursorPos(out var cursor))
            {
                _hoverArmed = !new Rect(px, py, physicalSize.Width, physicalSize.Height)
                    .Contains(new Point(cursor.X, cursor.Y));
            }
            ResetAutoHideTimer();
        }

        /// <summary>
        /// 隐藏红点
        /// </summary>
        public new void Hide()
        {
            _autoHideTimer.Stop();
            _hoverArmed = false;
            base.Hide();
        }

        /// <summary>
        /// 鼠标进入红点时触发翻译
        /// </summary>
        private void Grid_MouseEnter(object sender, MouseEventArgs e)
        {
            _autoHideTimer.Stop();
            if (!_hoverArmed)
            {
                // This can be the synthetic enter caused by showing the window
                // underneath the mouse. Require a real leave/re-enter cycle.
                ResetAutoHideTimer();
                return;
            }
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
