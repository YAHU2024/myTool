using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace QuickTranslate.Helpers
{
    /// <summary>
    /// DPI 缩放坐标转换工具。
    /// Win32 API 和 UI Automation 返回物理像素，WPF 的 Left/Top 使用逻辑像素(DIP)。
    /// 在 150% 等缩放比例下需进行转换。
    /// </summary>
    public static class DpiHelper
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        private const int LOGPIXELSX = 88;
        private const int LOGPIXELSY = 90;

        private static double? _scaleX;
        private static double? _scaleY;

        /// <summary>
        /// X 轴缩放比例（1.0 = 96 DPI，1.5 = 144 DPI 即 150% 缩放）
        /// </summary>
        public static double ScaleX
        {
            get
            {
                if (!_scaleX.HasValue) Refresh();
                return _scaleX ?? 1.0;
            }
        }

        /// <summary>
        /// Y 轴缩放比例
        /// </summary>
        public static double ScaleY
        {
            get
            {
                if (!_scaleY.HasValue) Refresh();
                return _scaleY ?? 1.0;
            }
        }

        /// <summary>
        /// 重新读取系统 DPI（显示器变更时调用）
        /// </summary>
        public static void Refresh()
        {
            var hdc = GetDC(IntPtr.Zero);
            try
            {
                _scaleX = GetDeviceCaps(hdc, LOGPIXELSX) / 96.0;
                _scaleY = GetDeviceCaps(hdc, LOGPIXELSY) / 96.0;
            }
            catch
            {
                _scaleX = 1.0;
                _scaleY = 1.0;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        /// <summary>
        /// 物理像素 → 逻辑像素(DIP)
        /// </summary>
        public static Point PhysicalToLogical(Point physical)
        {
            return new Point(physical.X / ScaleX, physical.Y / ScaleY);
        }

        /// <summary>
        /// 逻辑像素(DIP) → 物理像素
        /// </summary>
        public static Point LogicalToPhysical(Point logical)
        {
            return new Point(logical.X * ScaleX, logical.Y * ScaleY);
        }

        /// <summary>
        /// 物理像素矩形 → 逻辑像素矩形(DIP)
        /// </summary>
        public static Rect PhysicalToLogical(Rect physical)
        {
            return new Rect(
                physical.X / ScaleX,
                physical.Y / ScaleY,
                physical.Width / ScaleX,
                physical.Height / ScaleY);
        }

        /// <summary>
        /// 逻辑像素矩形(DIP) → 物理像素矩形
        /// </summary>
        public static Rect LogicalToPhysical(Rect logical)
        {
            return new Rect(
                logical.X * ScaleX,
                logical.Y * ScaleY,
                logical.Width * ScaleX,
                logical.Height * ScaleY);
        }
    }
}
