using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using QuickTranslate.Helpers;

namespace QuickTranslate.Core
{
    /// <summary>
    /// 全局键盘钩子 - 检测指定热键组合。
    /// 钩子安装在拥有独立原生消息循环的专用线程上，与 WPF Dispatcher 完全解耦。
    /// </summary>
    public class GlobalKeyboardHook : IDisposable
    {
        // 钩子专用线程
        private Thread? _hookThread;
        private uint _hookThreadId;
        private IntPtr _hookId = IntPtr.Zero;
        private readonly Win32Api.LowLevelKeyboardProc _proc;
        private readonly ManualResetEventSlim _threadReady = new(false);
        private volatile bool _isRunning;

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
        /// 热键触发时触发（在 WPF UI 线程上调用）
        /// </summary>
        public event Action? HotKeyPressed;

        public GlobalKeyboardHook()
        {
            _proc = KeyboardHookCallback;
        }

        /// <summary>
        /// 启动键盘钩子（创建专用消息循环线程）
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _threadReady.Reset();

            _hookThread = new Thread(HookThreadProc)
            {
                IsBackground = true,
                Name = "KeyboardHookThread"
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();
            _threadReady.Wait(3000);
        }

        /// <summary>
        /// 停止键盘钩子（退出消息循环，等待线程结束）
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;

            if (_hookThreadId != 0)
            {
                Win32Api.PostThreadMessage(_hookThreadId, Win32Api.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            _hookThread?.Join(2000);
            _hookThread = null;
            _hookThreadId = 0;
            Logger.Info("KeyboardHook", "全局键盘钩子已停止");
        }

        /// <summary>
        /// 钩子线程主函数：安装钩子 + 运行原生消息循环
        /// </summary>
        private void HookThreadProc()
        {
            _hookThreadId = (uint)Environment.CurrentManagedThreadId;

            var moduleHandle = Win32Api.GetModuleHandle(null);
            _hookId = Win32Api.SetWindowsHookEx(
                Win32Api.WH_KEYBOARD_LL, _proc, moduleHandle, 0);

            if (_hookId == IntPtr.Zero)
            {
                Logger.Error("KeyboardHook", $"键盘钩子安装失败，错误码: {Marshal.GetLastWin32Error()}");
                _threadReady.Set();
                return;
            }

            _threadReady.Set();
            Logger.Info("KeyboardHook", $"全局键盘钩子已启动（独立消息循环线程），热键: {GetHotKeyDisplay()}, HookId=0x{_hookId:X}, ThreadId={_hookThreadId}");

            // 原生消息循环
            try
            {
                int result;
                while ((result = Win32Api.GetMessage(out var msg, IntPtr.Zero, 0, 0)) != 0)
                {
                    if (result == -1)
                    {
                        Logger.Error("KeyboardHook", $"GetMessage 返回 -1，错误码: {Marshal.GetLastWin32Error()}");
                        break;
                    }
                    Win32Api.TranslateMessage(ref msg);
                    Win32Api.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                Logger.Fatal("KeyboardHook", "键盘钩子消息循环异常，线程即将退出", ex);
            }

            // 消息循环退出，清理钩子
            if (_hookId != IntPtr.Zero)
            {
                Win32Api.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            Logger.Warn("KeyboardHook", "键盘钩子消息循环已退出，钩子已卸载");
        }

        /// <summary>
        /// 键盘钩子回调（在钩子专用线程执行，带 try-catch 保护）
        /// </summary>
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, ref Win32Api.KBDLLHOOKSTRUCT lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    if (wParam == (IntPtr)Win32Api.WM_KEYDOWN)
                    {
                        // 过滤注入事件（模拟按键），避免 Ctrl+C 模拟触发钩子
                        if ((lParam.flags & Win32Api.LLKHF_INJECTED) != 0)
                        {
                            return Win32Api.CallNextHookEx(_hookId, nCode, wParam, ref lParam);
                        }

                        // 检查是否按下热键
                        if (lParam.vkCode == HotKey)
                        {
                            bool altPressed = (Win32Api.GetAsyncKeyState(0x12) & 0x8000) != 0;
                            bool ctrlPressed = (Win32Api.GetAsyncKeyState(Win32Api.VK_CONTROL) & 0x8000) != 0;
                            bool shiftPressed = (Win32Api.GetAsyncKeyState(0x10) & 0x8000) != 0;

                            bool match = true;
                            if (RequireAlt && !altPressed) match = false;
                            if (RequireCtrl && !ctrlPressed) match = false;
                            if (RequireShift && !shiftPressed) match = false;

                            if (match)
                            {
                                // 异步投递到 WPF UI 线程
                                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                                    new Action(() => HotKeyPressed?.Invoke()));
                            }
                        }
                    }

                    return Win32Api.CallNextHookEx(_hookId, nCode, wParam, ref lParam);
                }
                catch (Exception ex)
                {
                    Logger.Error("KeyboardHook", $"键盘钩子回调异常: wParam=0x{wParam:X}", ex);
                }
            }

            return Win32Api.CallNextHookEx(_hookId, nCode, wParam, ref lParam);
        }

        /// <summary>
        /// 获取热键的显示名称
        /// </summary>
        public string GetHotKeyDisplay()
        {
            var parts = new List<string>();
            if (RequireCtrl) parts.Add("Ctrl");
            if (RequireAlt) parts.Add("Alt");
            if (RequireShift) parts.Add("Shift");
            parts.Add(GetKeyName(HotKey));
            return string.Join("+", parts);
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
