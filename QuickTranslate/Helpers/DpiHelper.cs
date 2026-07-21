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

        private const uint MDT_EFFECTIVE_DPI = 0;
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, uint dpiType, out uint dpiX, out uint dpiY);

        /// <summary>
        /// X 轴缩放比例（1.0 = 96 DPI，1.5 = 144 DPI 即 150% 缩放）
        /// </summary>
        public static double ScaleX
        {
            get
            {
                return GetScaleForPhysicalPoint(new Point(0, 0)).X;
            }
        }

        /// <summary>
        /// Y 轴缩放比例
        /// </summary>
        public static double ScaleY
        {
            get
            {
                return GetScaleForPhysicalPoint(new Point(0, 0)).Y;
            }
        }

        /// <summary>
        /// 重新读取系统 DPI（显示器变更时调用）
        /// </summary>
        public static void Refresh()
        {
            // DPI is queried per monitor by the conversion methods.
        }

        /// <summary>
        /// 物理像素 → 逻辑像素(DIP)
        /// </summary>
        public static Point PhysicalToLogical(Point physical)
        {
            var scale = GetScaleForPhysicalPoint(physical);
            return new Point(physical.X / scale.X, physical.Y / scale.Y);
        }

        /// <summary>
        /// 逻辑像素(DIP) → 物理像素
        /// </summary>
        public static Point LogicalToPhysical(Point logical)
        {
            return new Point(logical.X * ScaleX, logical.Y * ScaleY);
        }

        public static Point GetScaleForPhysicalPoint(Point physical)
        {
            var monitor = Win32Api.MonitorFromPoint(new Win32Api.POINT { X = (int)physical.X, Y = (int)physical.Y }, Win32Api.MONITOR_DEFAULTTONEAREST);
            try
            {
                if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var x, out var y) == 0 && x > 0 && y > 0)
                    return new Point(x / 96.0, y / 96.0);
            }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
            var hdc = GetDC(IntPtr.Zero);
            try { return new Point(Math.Max(GetDeviceCaps(hdc, LOGPIXELSX) / 96.0, 1), Math.Max(GetDeviceCaps(hdc, LOGPIXELSY) / 96.0, 1)); }
            finally { if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc); }
        }

        public static Size LogicalSizeToPhysical(Size logical, Point physicalReference)
        {
            var scale = GetScaleForPhysicalPoint(physicalReference);
            return new Size(logical.Width * scale.X, logical.Height * scale.Y);
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
