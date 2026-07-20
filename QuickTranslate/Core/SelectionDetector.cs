using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using QuickTranslate.Helpers;

namespace QuickTranslate.Core
{
    /// <summary>
    /// 文本选择检测器 - 通过鼠标钩子检测拖拽选词和双击/三击选词操作。
    /// 钩子安装在拥有独立原生消息循环的专用线程上，彻底与 WPF Dispatcher 解耦，
    /// 避免 WPF 渲染/布局阻塞钩子回调导致鼠标卡顿或进程被 Windows 终止。
    /// </summary>
    public class SelectionDetector : IDisposable
    {
        // 钩子专用线程
        private Thread? _hookThread;
        private uint _hookThreadId;
        private IntPtr _mouseHookId = IntPtr.Zero;
        private readonly Win32Api.LowLevelMouseProc _mouseProc;
        private readonly ManualResetEventSlim _threadReady = new(false);
        private volatile bool _isRunning;

        // 鼠标状态（仅在钩子线程访问）
        private bool _isLeftButtonDown;
        private bool _isDragging;
        private Win32Api.POINT _mouseDownPos;
        private Win32Api.POINT _mouseUpPos;

        // 双击/三击检测（仅在钩子线程访问）
        private int _clickCount;
        private Win32Api.POINT _lastClickPos;
        private bool _multiClickCancelled;

        // 原生定时器（hWnd=NULL 时 SetTimer 返回系统分配的唯一 ID，必须用返回值匹配和销毁）
        private IntPtr _dragDelayTimerId = IntPtr.Zero;
        private IntPtr _multiClickTimerId = IntPtr.Zero;

        /// <summary>
        /// 红点是否可见（由 UI 线程设置，钩子线程读取；volatile 保证可见性）
        /// </summary>
        public volatile bool IsRedDotVisible;

        /// <summary>
        /// 检测到文本选择完成时触发（在 WPF UI 线程上调用）
        /// </summary>
        public event Action<Point, Point>? SelectionCompleted;

        /// <summary>
        /// 红点可见时用户点击其他位置触发（在 WPF UI 线程上调用）
        /// </summary>
        public event Action? ClickedOutside;

        public SelectionDetector()
        {
            _mouseProc = MouseHookCallback;
        }

        /// <summary>
        /// 启动鼠标钩子（创建专用消息循环线程）
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _threadReady.Reset();

            _hookThread = new Thread(HookThreadProc)
            {
                IsBackground = true,
                Name = "MouseHookThread"
            };
            _hookThread.Start();
            _threadReady.Wait(3000); // 等待钩子安装完成
        }

        /// <summary>
        /// 停止鼠标钩子（退出消息循环，等待线程结束）
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
            Logger.Info("SelectionDetector", "文本选择检测器已停止");
        }

        /// <summary>
        /// 钩子线程主函数：安装钩子 + 运行原生消息循环
        /// </summary>
        private void HookThreadProc()
        {
            _hookThreadId = (uint)Environment.CurrentManagedThreadId;

            var moduleHandle = Win32Api.GetModuleHandle(null);
            _mouseHookId = Win32Api.SetWindowsHookEx(
                Win32Api.WH_MOUSE_LL, _mouseProc, moduleHandle, 0);

            if (_mouseHookId == IntPtr.Zero)
            {
                Logger.Error("SelectionDetector", $"鼠标钩子安装失败，错误码: {Marshal.GetLastWin32Error()}");
                _threadReady.Set();
                return;
            }

            _threadReady.Set();
            Logger.Info("SelectionDetector", $"文本选择检测器已启动（独立消息循环线程），HookId=0x{_mouseHookId:X}, ThreadId={_hookThreadId}");

            // 原生消息循环 —— 保证钩子回调立即被调度，不受 WPF Dispatcher 影响
            try
            {
                int result;
                while ((result = Win32Api.GetMessage(out var msg, IntPtr.Zero, 0, 0)) != 0)
                {
                    if (result == -1)
                    {
                        // GetMessage 失败，记录错误并退出循环，避免分发垃圾消息导致崩溃
                        Logger.Error("SelectionDetector", $"GetMessage 返回 -1，错误码: {Marshal.GetLastWin32Error()}");
                        break;
                    }

                    if (msg.message == Win32Api.WM_TIMER)
                    {
                        HandleTimer(msg.wParam);
                    }
                    Win32Api.TranslateMessage(ref msg);
                    Win32Api.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                Logger.Fatal("SelectionDetector", "鼠标钩子消息循环异常，线程即将退出", ex);
            }

            // 消息循环退出，清理钩子
            if (_mouseHookId != IntPtr.Zero)
            {
                Win32Api.UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }
            Logger.Warn("SelectionDetector", "鼠标钩子消息循环已退出，钩子已卸载");
        }

        /// <summary>
        /// 鼠标钩子回调（在钩子专用线程执行，微秒级返回，不阻塞鼠标输入）
        /// </summary>
        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, ref Win32Api.MSLLHOOKSTRUCT lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    // 过滤注入事件（避免模拟鼠标操作触发自身）
                    if ((lParam.flags & Win32Api.LLMHF_INJECTED) != 0)
                    {
                        return Win32Api.CallNextHookEx(_mouseHookId, nCode, wParam, ref lParam);
                    }

                    if (wParam == (IntPtr)Win32Api.WM_LBUTTONDOWN)
                    {
                        // 红点可见时点击 = 取消红点（异步投递到 UI 线程）
                        if (IsRedDotVisible)
                        {
                            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                ClickedOutside?.Invoke();
                            }));
                        }

                        // 取消所有待处理定时器
                        KillDragTimer();
                        KillMultiClickTimer();
                        _multiClickCancelled = true;

                        _isLeftButtonDown = true;
                        _isDragging = false;
                        _mouseDownPos = lParam.pt;
                    }
                    else if (wParam == (IntPtr)Win32Api.WM_MOUSEMOVE && _isLeftButtonDown)
                    {
                        // 物理像素平方距离判断（无 DPI 转换、无浮点除法）
                        int dx = lParam.pt.X - _mouseDownPos.X;
                        int dy = lParam.pt.Y - _mouseDownPos.Y;
                        if (dx * dx + dy * dy > 100) // >10px 阈值
                        {
                            _isDragging = true;
                        }
                    }
                    else if (wParam == (IntPtr)Win32Api.WM_LBUTTONUP)
                    {
                        _mouseUpPos = lParam.pt;

                        if (_isDragging)
                        {
                            // 拖拽完成：150ms 延迟给目标应用时间完成选区处理
                            KillDragTimer();
                            _dragDelayTimerId = Win32Api.SetTimer(IntPtr.Zero, IntPtr.Zero, 150, IntPtr.Zero);
                            _clickCount = 0;
                        }
                        else
                        {
                            // 非拖拽：双击/三击检测
                            int dx = lParam.pt.X - _lastClickPos.X;
                            int dy = lParam.pt.Y - _lastClickPos.Y;
                            _clickCount = (dx * dx + dy * dy < 100) ? _clickCount + 1 : 1;
                            _lastClickPos = lParam.pt;

                            if (_clickCount >= 2)
                            {
                                _multiClickCancelled = false;
                                KillMultiClickTimer();
                                _multiClickTimerId = Win32Api.SetTimer(IntPtr.Zero, IntPtr.Zero, 100, IntPtr.Zero);
                            }
                        }

                        _isLeftButtonDown = false;
                        _isDragging = false;
                    }

                    return Win32Api.CallNextHookEx(_mouseHookId, nCode, wParam, ref lParam);
                }
                catch (Exception ex)
                {
                    Logger.Error("SelectionDetector", $"鼠标钩子回调异常: wParam=0x{wParam:X}", ex);
                }
            }

            return Win32Api.CallNextHookEx(_mouseHookId, nCode, wParam, ref lParam);
        }

        /// <summary>
        /// 处理原生 WM_TIMER 消息（在钩子线程消息循环中执行）
        /// </summary>
        private void HandleTimer(IntPtr timerId)
        {
            try
            {
                if (timerId == _dragDelayTimerId && _dragDelayTimerId != IntPtr.Zero)
                {
                    KillDragTimer();

                    // 转换为逻辑像素并投递到 UI 线程
                    var startPos = DpiHelper.PhysicalToLogical(
                        new Point(_mouseDownPos.X, _mouseDownPos.Y));
                    var endPos = DpiHelper.PhysicalToLogical(
                        new Point(_mouseUpPos.X, _mouseUpPos.Y));

                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SelectionCompleted?.Invoke(startPos, endPos);
                    }));
                }
                else if (timerId == _multiClickTimerId && _multiClickTimerId != IntPtr.Zero)
                {
                    KillMultiClickTimer();

                    if (_multiClickCancelled || _isDragging) return;

                    var clickPos = DpiHelper.PhysicalToLogical(
                        new Point(_lastClickPos.X, _lastClickPos.Y));

                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SelectionCompleted?.Invoke(clickPos, clickPos);
                    }));
                    _clickCount = 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("SelectionDetector", $"定时器处理异常: {ex.Message}");
            }
        }

        private void KillDragTimer()
        {
            if (_dragDelayTimerId != IntPtr.Zero)
            {
                Win32Api.KillTimer(IntPtr.Zero, _dragDelayTimerId);
                _dragDelayTimerId = IntPtr.Zero;
            }
        }

        private void KillMultiClickTimer()
        {
            if (_multiClickTimerId != IntPtr.Zero)
            {
                Win32Api.KillTimer(IntPtr.Zero, _multiClickTimerId);
                _multiClickTimerId = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
