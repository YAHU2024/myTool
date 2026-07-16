using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using QuickTranslate.Helpers;

namespace QuickTranslate.Core
{
    /// <summary>
    /// 全局键盘钩子 - 检测指定热键组合
    /// </summary>
    public class GlobalKeyboardHook : IDisposable
    {
        private IntPtr _hookId = IntPtr.Zero;
        private readonly Win32Api.LowLevelKeyboardProc _proc;
        private bool _isStarted;

        /// <summary>
        /// 热键的虚拟键码（默认 VK_Q = 0x51）
        /// </summary>
        public byte HotKey { get; set; } = Win32Api.VK_Q;

        /// <summary>
        /// 是否需要 Alt 修饰键（默认 true）
        /// </summary>
        public bool RequireAlt { get; set; } = true;

        /// <summary>
        /// 是否需要 Ctrl 修饰键（默认 false）
        /// </summary>
        public bool RequireCtrl { get; set; } = false;

        /// <summary>
        /// 是否需要 Shift 修饰键（默认 false）
        /// </summary>
        public bool RequireShift { get; set; } = false;

        /// <summary>
        /// 热键触发时触发
        /// </summary>
        public event Action? HotKeyPressed;

        public GlobalKeyboardHook()
        {
            _proc = KeyboardHookCallback;
        }

        /// <summary>
        /// 获取热键的显示名称
        /// </summary>
        public string GetHotKeyDisplay()
        {
            var parts = new System.Collections.Generic.List<string>();
            if (RequireCtrl) parts.Add("Ctrl");
            if (RequireAlt) parts.Add("Alt");
            if (RequireShift) parts.Add("Shift");
            parts.Add(GetKeyName(HotKey));
            return string.Join("+", parts);
        }

        /// <summary>
        /// 启动键盘钩子
        /// </summary>
        public void Start()
        {
            if (_isStarted) return;

            var moduleHandle = Win32Api.GetModuleHandle(null);
            _hookId = Win32Api.SetWindowsHookEx(
                Win32Api.WH_KEYBOARD_LL,
                _proc,
                moduleHandle,
                0);

            if (_hookId == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Debug.WriteLine($"设置键盘钩子失败，错误码: {error}");
                return;
            }

            _isStarted = true;
            Debug.WriteLine($"全局键盘钩子已启动，热键: {GetHotKeyDisplay()}");
        }

        /// <summary>
        /// 停止键盘钩子
        /// </summary>
        public void Stop()
        {
            if (!_isStarted) return;

            if (_hookId != IntPtr.Zero)
            {
                Win32Api.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }

            _isStarted = false;
            Debug.WriteLine("全局键盘钩子已停止");
        }

        /// <summary>
        /// 键盘钩子回调
        /// </summary>
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, ref Win32Api.KBDLLHOOKSTRUCT lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)Win32Api.WM_KEYDOWN)
            {
                // 过滤注入事件（模拟按键），避免 Ctrl+C 模拟触发钩子
                if ((lParam.flags & Win32Api.LLKHF_INJECTED) != 0)
                {
                    return Win32Api.CallNextHookEx(_hookId, nCode, wParam, ref lParam);
                }

                // 检查是否按下热键
                if (lParam.vkCode == HotKey)
                {
                    bool altPressed = (Win32Api.GetAsyncKeyState(0x12) & 0x8000) != 0; // VK_MENU = Alt
                    bool ctrlPressed = (Win32Api.GetAsyncKeyState(Win32Api.VK_CONTROL) & 0x8000) != 0;
                    bool shiftPressed = (Win32Api.GetAsyncKeyState(0x10) & 0x8000) != 0; // VK_SHIFT

                    bool match = true;
                    if (RequireAlt && !altPressed) match = false;
                    if (RequireCtrl && !ctrlPressed) match = false;
                    if (RequireShift && !shiftPressed) match = false;

                    if (match)
                    {
                        // 在 UI 线程触发事件
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                            new Action(() => HotKeyPressed?.Invoke()));
                    }
                }
            }

            return Win32Api.CallNextHookEx(_hookId, nCode, wParam, ref lParam);
        }

        /// <summary>
        /// 获取按键名称
        /// </summary>
        private static string GetKeyName(byte vk)
        {
            return vk switch
            {
                0x51 => "Q",
                0x41 => "A",
                0x5A => "Z",
                0x20 => "Space",
                _ => $"VK_{vk:X2}"
            };
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
