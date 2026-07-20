using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace QuickTranslate.Helpers
{
    /// <summary>
    /// Win32 API P/Invoke 声明
    /// </summary>
    internal static class Win32Api
    {
        // 常量
        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int VK_SPACE = 0x20;
        public const int VK_CONTROL = 0x11;
        public const byte VK_C = 0x43;
        public const byte VK_Q = 0x51;
        public const byte KEYEVENTF_KEYUP = 0x02;

        // 键盘钩子标志（用于过滤模拟按键事件）
        public const uint LLKHF_INJECTED = 0x10;

        // 鼠标钩子标志（用于过滤模拟鼠标事件）
        public const uint LLMHF_INJECTED = 0x01;

        // 委托（必须持有引用，防止 GC 回收）
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, ref KBDLLHOOKSTRUCT lParam);
        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, ref MSLLHOOKSTRUCT lParam);

        // 结构体
        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // 键盘钩子
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, ref KBDLLHOOKSTRUCT lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, ref MSLLHOOKSTRUCT lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        // 模拟按键
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, byte dwFlags, UIntPtr dwExtraInfo);

        // 鼠标位置
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        // 前台窗口
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        // 按键状态检测
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        // ── 原生消息循环（钩子专用线程） ──
        [DllImport("user32.dll")]
        public static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern void PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        // ── 原生定时器（钩子线程内使用） ──
        public const uint WM_TIMER = 0x0113;
        public const uint WM_QUIT = 0x0012;

        [DllImport("user32.dll")]
        public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        // ── 多显示器支持 ──
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        /// <summary>
        /// 获取指定屏幕坐标所在显示器的工作区矩形。
        /// 输入为 WPF 逻辑像素(DIP)，内部转为物理像素调用 API，返回值为 DIP。
        /// </summary>
        public static Rect GetWorkAreaAtPoint(Point screenPoint)
        {
            // DIP → 物理像素
            var physical = DpiHelper.LogicalToPhysical(screenPoint);
            var pt = new POINT { X = (int)physical.X, Y = (int)physical.Y };
            var hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(hMon, ref mi);
            // 物理像素 → DIP
            return DpiHelper.PhysicalToLogical(
                new Rect(mi.rcWork.Left, mi.rcWork.Top,
                    mi.rcWork.Right - mi.rcWork.Left,
                    mi.rcWork.Bottom - mi.rcWork.Top));
        }
    }
}
