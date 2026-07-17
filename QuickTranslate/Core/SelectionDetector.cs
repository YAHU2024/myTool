using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using QuickTranslate.Helpers;

namespace QuickTranslate.Core
{
    /// <summary>
    /// 文本选择检测器 - 通过鼠标钩子检测拖拽选词和双击/三击选词操作
    /// </summary>
    public class SelectionDetector : IDisposable
    {
        private IntPtr _mouseHookId = IntPtr.Zero;
        private readonly Win32Api.LowLevelMouseProc _mouseProc;
        private bool _isStarted;
        private bool _isLeftButtonDown;
        private bool _isDragging;
        private System.Windows.Point _mouseDownPos;

        // 双击/三击选词检测
        private int _clickCount;
        private System.Windows.Point _lastClickPos;
        private DispatcherTimer? _multiClickTimer;
        private CancellationTokenSource? _multiClickCts;

        /// <summary>
        /// 红点是否可见（由外部设置，用于判断点击是否应取消红点）
        /// </summary>
        public bool IsRedDotVisible { get; set; }

        /// <summary>
        /// 检测到文本选择完成时触发（参数1=拖拽起点，参数2=拖拽终点）
        /// </summary>
        public event Action<System.Windows.Point, System.Windows.Point>? SelectionCompleted;

        /// <summary>
        /// 红点可见时用户点击其他位置触发（取消红点）
        /// </summary>
        public event Action? ClickedOutside;

        public SelectionDetector()
        {
            _mouseProc = MouseHookCallback;
        }

        /// <summary>
        /// 启动鼠标钩子
        /// </summary>
        public void Start()
        {
            if (_isStarted) return;

            var moduleHandle = Win32Api.GetModuleHandle(null);
            _mouseHookId = Win32Api.SetWindowsHookEx(
                Win32Api.WH_MOUSE_LL,
                _mouseProc,
                moduleHandle,
                0);

            if (_mouseHookId == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Debug.WriteLine($"设置鼠标钩子失败，错误码: {error}");
                return;
            }

            _isStarted = true;
            Debug.WriteLine("文本选择检测器已启动");
        }

        /// <summary>
        /// 停止鼠标钩子
        /// </summary>
        public void Stop()
        {
            if (!_isStarted) return;

            if (_mouseHookId != IntPtr.Zero)
            {
                Win32Api.UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }

            _isStarted = false;
            Debug.WriteLine("文本选择检测器已停止");
        }

        /// <summary>
        /// 鼠标钩子回调
        /// </summary>
        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, ref Win32Api.MSLLHOOKSTRUCT lParam)
        {
            if (nCode >= 0)
            {
                // 过滤注入事件
                if ((lParam.flags & Win32Api.LLKHF_INJECTED) != 0)
                {
                    return Win32Api.CallNextHookEx(_mouseHookId, nCode, wParam, ref lParam);
                }

                // 鼠标钩子返回物理像素，转换为 WPF 逻辑像素(DIP)
                var pos = DpiHelper.PhysicalToLogical(
                    new System.Windows.Point(lParam.pt.X, lParam.pt.Y));

                if (wParam == (IntPtr)Win32Api.WM_LBUTTONDOWN)
                {
                    // 红点可见时点击 = 取消红点
                    if (IsRedDotVisible)
                    {
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            ClickedOutside?.Invoke();
                        }));
                    }

                    // 取消待处理的双击/三击检测
                    _multiClickCts?.Cancel();
                    _multiClickCts?.Dispose();
                    _multiClickCts = null;
                    _multiClickTimer?.Stop();

                    _isLeftButtonDown = true;
                    _isDragging = false;
                    _mouseDownPos = pos;
                }
                else if (wParam == (IntPtr)Win32Api.WM_MOUSEMOVE && _isLeftButtonDown)
                {
                    // 判断是否是拖拽操作（移动距离 > 10px）
                    var dx = pos.X - _mouseDownPos.X;
                    var dy = pos.Y - _mouseDownPos.Y;
                    if (Math.Sqrt(dx * dx + dy * dy) > 10)
                    {
                        _isDragging = true;
                    }
                }
                else if (wParam == (IntPtr)Win32Api.WM_LBUTTONUP)
                {
                    if (_isDragging)
                    {
                        // 拖拽选择完成，传递起点和终点用于红点定位
                        var startPos = _mouseDownPos;
                        var endPos = pos;
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            SelectionCompleted?.Invoke(startPos, endPos);
                        }));
                        _clickCount = 0;
                    }
                    else
                    {
                        // 非拖拽释放，检查是否是双击/三击选词
                        var dx = pos.X - _lastClickPos.X;
                        var dy = pos.Y - _lastClickPos.Y;
                        if (Math.Sqrt(dx * dx + dy * dy) < 10)
                        {
                            _clickCount++;
                        }
                        else
                        {
                            _clickCount = 1;
                        }
                        _lastClickPos = pos;

                        if (_clickCount >= 2)
                        {
                            // 双击/三击：延迟检查剪贴板是否有选中文本
                            _multiClickCts?.Cancel();
                            _multiClickCts?.Dispose();
                            _multiClickCts = new CancellationTokenSource();
                            var token = _multiClickCts.Token;
                            var clickPos = pos;
                            var clickCount = _clickCount;

                            _multiClickTimer?.Stop();
                            _multiClickTimer = new DispatcherTimer
                            {
                                Interval = TimeSpan.FromMilliseconds(100)
                            };
                            _multiClickTimer.Tick += async (s, e) =>
                            {
                                ((DispatcherTimer)s!).Stop();
                                if (token.IsCancellationRequested || _isDragging) return;

                                // 双击/三击选词完成，检查剪贴板
                                var selectedText = await ClipboardHelper.GetSelectedTextAsync();
                                if (!string.IsNullOrWhiteSpace(selectedText) && !token.IsCancellationRequested && !_isDragging)
                                {
                                    Debug.WriteLine($"双击/三击选词检测到 (clicks={clickCount})");
                                    // 双击/三击选词：起点=终点=点击位置
                                    SelectionCompleted?.Invoke(clickPos, clickPos);
                                }
                                _clickCount = 0;
                            };
                            _multiClickTimer.Start();
                        }
                    }
                    _isLeftButtonDown = false;
                    _isDragging = false;
                }
            }

            return Win32Api.CallNextHookEx(_mouseHookId, nCode, wParam, ref lParam);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
