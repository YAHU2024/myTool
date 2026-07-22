using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using QuickTranslate.Core;
using QuickTranslate.Database;
using QuickTranslate.Helpers;
using QuickTranslate.Models;
using QuickTranslate.Services;
using QuickTranslate.UI;

namespace QuickTranslate;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private GlobalKeyboardHook? _keyboardHook;
    private SelectionDetector? _selectionDetector;
    private OpenAITranslationService? _translationService;
    private AppSettings? _settings;
    private FloatingWindow? _floatingWindow;
    private RedDotWindow? _redDotWindow;
    private TrayIconManager? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private TranslationDbContext? _dbContext;
    private bool _isTranslating;
    private long _selectionGeneration;
    private CancellationTokenSource? _selectionCts;
    private ForegroundWindowInfo? _pendingSelection;
    private Mutex? _singleInstanceMutex;
    private Window? _hiddenWindow; // 隐藏主窗口，稳定 WPF Application 生命周期
    private Timer? _watchdogTimer; // 看门狗线程，定期写入状态文件

    // 非托管异常处理
    [DllImport("kernel32.dll")]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredExceptionHandler handler);
    private delegate IntPtr VectoredExceptionHandler(IntPtr exceptionInfo);
    private static VectoredExceptionHandler? _vehHandler; // 防止 GC 回收

    // 控制台信号处理
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);
    private delegate bool ConsoleCtrlHandler(uint ctrlType);
    private static ConsoleCtrlHandler? _ctrlHandler; // 防止 GC 回收

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 加载配置
        _settings = ConfigManager.Load();

        // 初始化日志系统
#if DEBUG
        // 附加控制台以实时输出日志（接受信号风险，但有 CtrlHandler 保护）
        Win32Api.AttachConsole(Win32Api.ATTACH_PARENT_PROCESS);
        // 注册控制台信号处理器，忽略 Ctrl+C/Ctrl+Break/关闭信号
        _ctrlHandler = ConsoleCtrlCallback;
        SetConsoleCtrlHandler(_ctrlHandler, true);
#endif
        Logger.Init(
            minLevel: Logger.ParseLevel(_settings.LogLevel),
            retentionDays: _settings.LogRetentionDays);
        Logger.Info("App", $"应用启动, OS={Environment.OSVersion}, .NET={Environment.Version}");

        // ★ 启动时清扫上次残留的剪贴板哨兵
        ClipboardHelper.CleanResidualOnStartup();

        // ★ 单实例保护：防止双击启动第二个实例导致钩子冲突
        bool createdNew;
        _singleInstanceMutex = new Mutex(true, "QuickTranslate_SingleInstance_v1", out createdNew);
        if (!createdNew)
        {
            Logger.Warn("App", "检测到已有实例运行，退出新实例");
            Shutdown();
            return;
        }

        // ★ 全路径退出监控（诊断层）
        Dispatcher.ShutdownStarted += (s, ev) =>
        {
            Logger.Fatal("App", $"Dispatcher.ShutdownStarted (HasShutdownStarted={Dispatcher.HasShutdownStarted})");
            Logger.Shutdown();
        };
        Dispatcher.ShutdownFinished += (s, ev) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Logger.LogDirectory, "shutdown-trace.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Dispatcher.ShutdownFinished\n");
            }
            catch { }
        };
        AppDomain.CurrentDomain.ProcessExit += (s, ev) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Logger.LogDirectory, "shutdown-trace.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ProcessExit\n");
            }
            catch { }
        };

        // 全局异常兖底，防止未捕获异常导致闪退
        DispatcherUnhandledException += (s, args) =>
        {
            Logger.Fatal("App", "未处理异常(UI线程)", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Logger.Fatal("App", $"未处理异常(AppDomain): {args.ExceptionObject}");
        };

        // 初始化数据库
        _dbContext = new TranslationDbContext();
        _dbContext.EnsureDatabaseCreated();

        // 初始化翻译服务
        _translationService = new OpenAITranslationService(_settings);

        // 初始化悬浮窗（单例复用）
        _floatingWindow = new FloatingWindow();
        _floatingWindow.AnalysisRequested += OnAnalysisRequested;

        // 初始化红点窗口（单例复用）
        _redDotWindow = new RedDotWindow();
        _redDotWindow.HoverTriggered += OnRedDotHovered;
        _redDotWindow.Cancelled += OnSelectionCancelled;

        // 启动全局键盘钩子
        _keyboardHook = new GlobalKeyboardHook();
        _keyboardHook.HotKey = _settings.HotKeyVK;
        _keyboardHook.RequireAlt = _settings.HotKeyRequireAlt;
        _keyboardHook.RequireCtrl = _settings.HotKeyRequireCtrl;
        _keyboardHook.RequireShift = _settings.HotKeyRequireShift;
        _keyboardHook.HotKeyPressed += OnHotKeyPressed;
        if (_settings.HotKeyEnabled)
        {
            _keyboardHook.Start();
        }

        // 启动文本选择检测器
        _selectionDetector = new SelectionDetector();
        _selectionDetector.SelectionCompleted += OnSelectionCompleted;
        _selectionDetector.ClickedOutside += OnSelectionCancelled;
        _selectionDetector.Start();

        // 初始化系统托盘图标
        _trayIcon = new TrayIconManager();
        _trayIcon.SettingsRequested += OnSettingsRequested;
        _trayIcon.HistoryRequested += OnHistoryRequested;
        _trayIcon.PauseToggled += OnPauseToggled;
        _trayIcon.HotKeyToggled += OnHotKeyToggled;
        _trayIcon.ExitRequested += OnExitRequested;

        // 根据配置初始化快捷键开关状态
        _trayIcon.SetHotKeyEnabled(_settings.HotKeyEnabled);

        // 根据配置更新托盘提示
        UpdateTrayToolTip();

        // ★ 注册非托管异常处理器（捕获 access violation 等 native 异常）
        _vehHandler = VehCallback;
        AddVectoredExceptionHandler(1, _vehHandler);

        // ★ 看门狗线程：每 2 秒写入状态文件，用于定位进程死亡时刻
        var tracePath = Path.Combine(Logger.LogDirectory, "watchdog.trace");
        _watchdogTimer = new Timer(_ =>
        {
            try
            {
                File.WriteAllText(tracePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] alive, thread={Thread.CurrentThread.ManagedThreadId}\n");
            }
            catch { }
        }, null, 0, 2000);

        // 启动后直接最小化到托盘，不显示主窗口
        // ★ 创建隐藏主窗口：稳定 WPF Application 生命周期 + 接收 Shell 激活消息
        _hiddenWindow = new Window
        {
            Width = 0, Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Visibility = Visibility.Hidden
        };
        _hiddenWindow.Show();
    }

    /// <summary>
    /// 非托管异常向量处理器（在 CLR 异常处理之前执行，可捕获 access violation）
    /// </summary>
    private static IntPtr VehCallback(IntPtr exceptionInfo)
    {
        try
        {
            // EXCEPTION_POINTERS 结构: [ExceptionRecord*][ContextRecord*]
            var excRecord = Marshal.ReadIntPtr(exceptionInfo);
            var exceptionCode = Marshal.ReadInt32(excRecord); // ExceptionCode 在偏移 0
            File.AppendAllText(
                Path.Combine(Logger.LogDirectory, "shutdown-trace.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] VEH: native exception code=0x{exceptionCode:X8}\n");
        }
        catch { }
        return IntPtr.Zero; // EXCEPTION_CONTINUE_SEARCH
    }

    /// <summary>
    /// 控制台信号处理器（忽略 Ctrl+C/Ctrl+Break/关闭，防止进程被杀）
    /// </summary>
    private static bool ConsoleCtrlCallback(uint ctrlType)
    {
        const uint CTRL_C_EVENT = 0;
        const uint CTRL_BREAK_EVENT = 1;
        const uint CTRL_CLOSE_EVENT = 2;

        try
        {
            File.AppendAllText(
                Path.Combine(Logger.LogDirectory, "shutdown-trace.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ConsoleCtrl: type={ctrlType}\n");
        }
        catch { }

        // 对 Ctrl+C/Break/Close 返回 true（已处理，不终止进程）
        if (ctrlType == CTRL_C_EVENT || ctrlType == CTRL_BREAK_EVENT || ctrlType == CTRL_CLOSE_EVENT)
            return true;
        return false;
    }

    /// <summary>
    /// 检查翻译功能是否启用
    /// </summary>
    private bool IsTranslationEnabled => _settings?.TranslationEnabled ?? true;

    /// <summary>
    /// 热键事件处理（默认 Alt+Q）
    /// </summary>
    private async void OnHotKeyPressed()
    {
        if (!IsTranslationEnabled) return;
        if (_isTranslating || _translationService == null || _settings == null || _floatingWindow == null)
            return;

        var sourceWindow = TerminalDetector.CaptureForegroundWindow();

        // 浏览器中禁用翻译：避免与浏览器翻译插件冲突
        if (!_settings.EnableInBrowser && BrowserDetector.IsForegroundBrowser(_settings.CustomBrowserProcesses))
        {
            Logger.Debug("App", "热键触发但前台为浏览器，已跳过（浏览器翻译已禁用）");
            return;
        }

        _isTranslating = true;

        try
        {
            if (!TerminalDetector.TryCreateCopyRequest(sourceWindow, _settings, out var copyRequest, out var rejectionMessage))
            {
                _floatingWindow.ShowTranslation(rejectionMessage ?? "无法安全获取选中文本", await GetSelectionAnchorPositionAsync());
                return;
            }

            // Copy before the optional UIA lookup so focus cannot change during an async operation.
            var selectedText = await ClipboardHelper.GetSelectedTextAsync(copyRequest!);
            var anchorPosition = await GetSelectionAnchorPositionAsync();

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                // 先显示悬浮窗再更新提示
                _floatingWindow.ShowTranslation("请先选中要翻译的文本", anchorPosition);
                return;
            }

            // 智能内容检测（仅在 SmartContentType 开启时执行）—— 提前到翻译前
            var contentType = _settings.SmartContentType
                ? ContentTypeDetector.Detect(selectedText)
                : ContentType.Translation;

            // 显示悬浮窗 + 类型标签（与 "翻译中..." 同时出现）
            _floatingWindow.ShowTranslation("翻译中...", anchorPosition);
            _floatingWindow.ShowContentTypeLabel(contentType);

            // 流式翻译
            var targetLang = _settings.TargetLanguage;
            // ★ BeginInvoke 异步投递：不阻塞后台流式读取线程，让 Dispatcher 有时间渲染
            var result = await _translationService.TranslateStreamingAsync(
                selectedText,
                targetLang,
                chunk => Dispatcher.BeginInvoke(() =>
                {
                    _floatingWindow.UpdateTranslation(chunk);
                }),
                contentType,
                onFallbackUsed: () => Dispatcher.BeginInvoke(() =>
                    _floatingWindow.ShowAnalysisTag(selectedText)));

            // 保存翻译历史
            SaveTranslationHistory(selectedText, result, targetLang, contentType);

            Logger.Info("App", $"热键翻译完成: {result.Length} 字");
        }
        catch (Exception ex)
        {
            Logger.Error("App", "热键翻译出错", ex);
            if (_floatingWindow != null)
            {
                _floatingWindow.UpdateTranslation($"翻译失败: {ex.Message}");
            }
        }
        finally
        {
            _isTranslating = false;
        }
    }

    /// <summary>
    /// 文本选择完成事件处理 - 显示红点。
    /// 防重入：如果上一次操作尚未完成，直接丢弃新触发。
    /// </summary>
    private async void OnSelectionCompleted(System.Windows.Point startPos, System.Windows.Point endPos)
    {
        var generation = Interlocked.Increment(ref _selectionGeneration);
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        var cts = new CancellationTokenSource();
        _selectionCts = cts;
        var token = cts.Token;

        try
        {
            if (!IsTranslationEnabled) return;
            if (_redDotWindow == null) return;

            // 浏览器中禁用翻译：避免与浏览器翻译插件冲突
            if (_settings != null && !_settings.EnableInBrowser && BrowserDetector.IsForegroundBrowser(_settings.CustomBrowserProcesses))
            {
                Logger.Debug("App", "选词触发但前台为浏览器，已跳过（浏览器翻译已禁用）");
                return;
            }

            var sourceWindow = TerminalDetector.CaptureForegroundWindow();
            if (sourceWindow == null) return;

            // 尝试 UIA 异步精确定位（不阻塞 UI 线程）
            var location = await SelectionLocator.TryGetSelectionBoundsAsync(2000, token);
            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _selectionGeneration)) return;
            if (Win32Api.GetForegroundWindow() != sourceWindow.Handle) return;
            if (location == null || !location.IsValid)
            {
                // Fallback: physical drag end point, same coordinate contract as UIA.
                var mid = endPos;
                location = new SelectionLocation
                {
                    IsValid = false,
                    FallbackPoint = mid
                };
            }

            // Defer clipboard access until the user deliberately hovers the red dot.
            _pendingSelection = sourceWindow;
            // 显示红点
            _redDotWindow.ShowAt(location);
            _selectionDetector!.IsRedDotVisible = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error("App", "OnSelectionCompleted 异常", ex);
        }
        finally { if (ReferenceEquals(_selectionCts, cts)) _selectionCts = null; cts.Dispose(); }
    }

    /// <summary>
    /// 红点悬停事件处理 - 触发翻译
    /// </summary>
    private async void OnRedDotHovered()
    {
        if (!IsTranslationEnabled) return;
        if (_isTranslating || _translationService == null || _settings == null || _floatingWindow == null)
            return;

        if (_pendingSelection == null) return;

        // 标记红点已隐藏
        _selectionDetector!.IsRedDotVisible = false;
        _redDotWindow?.Hide();

        _isTranslating = true;
        var sourceWindow = _pendingSelection;
        _pendingSelection = null;

        try
        {
            if (!TerminalDetector.TryCreateCopyRequest(sourceWindow, _settings, out var copyRequest, out var rejectionMessage))
            {
                _floatingWindow.ShowTranslation(rejectionMessage ?? "无法安全获取选中文本", _redDotWindow?.DotScreenPosition ?? new System.Windows.Point(0, 0));
                return;
            }

            var textToTranslate = await ClipboardHelper.GetSelectedTextAsync(copyRequest!);
            if (string.IsNullOrWhiteSpace(textToTranslate))
            {
                _floatingWindow.ShowTranslation("请保持原窗口焦点并选中要翻译的文本", _redDotWindow?.DotScreenPosition ?? new System.Windows.Point(0, 0));
                return;
            }

            // 使用红点位置（而非鼠标位置）作为悬浮窗显示位置
            var dotPosition = _redDotWindow?.DotScreenPosition
                ?? new System.Windows.Point(0, 0);

            // 智能内容检测（仅在 SmartContentType 开启时执行）—— 提前到翻译前
            var contentType = _settings.SmartContentType
                ? ContentTypeDetector.Detect(textToTranslate)
                : ContentType.Translation;

            // 显示悬浮窗 + 类型标签（与 "翻译中..." 同时出现）
            _floatingWindow.ShowTranslation("翻译中...", dotPosition);
            _floatingWindow.ShowContentTypeLabel(contentType);

            // 流式翻译
            var targetLang = _settings.TargetLanguage;
            // ★ BeginInvoke 异步投递：不阻塞后台流式读取线程
            var result = await _translationService.TranslateStreamingAsync(
                textToTranslate,
                targetLang,
                chunk => Dispatcher.BeginInvoke(() =>
                {
                    _floatingWindow.UpdateTranslation(chunk);
                }),
                contentType,
                onFallbackUsed: () => Dispatcher.BeginInvoke(() =>
                    _floatingWindow.ShowAnalysisTag(textToTranslate)));

            // 保存翻译历史
            SaveTranslationHistory(textToTranslate, result, targetLang, contentType);

            Logger.Info("App", $"红点翻译完成: {result.Length} 字");
        }
        catch (Exception ex)
        {
            Logger.Error("App", "红点翻译出错", ex);
            if (_floatingWindow != null)
            {
                _floatingWindow.UpdateTranslation($"翻译失败: {ex.Message}");
            }
        }
        finally
        {
            _isTranslating = false;
        }
    }

    /// <summary>
    /// 用户点击[解析]标签触发深度解析
    /// </summary>
    private async void OnAnalysisRequested(string sourceText)
    {
        if (_isTranslating || _translationService == null || _settings == null || _floatingWindow == null)
            return;

        if (string.IsNullOrWhiteSpace(sourceText))
            return;

        _isTranslating = true;

        try
        {
            // 显示解析中状态
            _floatingWindow.UpdateTranslation("解析中...");
            _floatingWindow.ShowContentTypeLabel(ContentType.Analysis);

            var targetLang = _settings.TargetLanguage;

            // 流式解析
            var result = await _translationService.AnalyzeStreamingAsync(
                sourceText,
                targetLang,
                chunk => Dispatcher.BeginInvoke(() =>
                {
                    _floatingWindow.UpdateTranslation(chunk);
                }));

            // 保存解析历史
            SaveTranslationHistory(sourceText, result, targetLang, ContentType.Analysis);

            Logger.Info("App", $"解析完成: {result.Length} 字");
        }
        catch (Exception ex)
        {
            Logger.Error("App", "解析出错", ex);
            _floatingWindow.UpdateTranslation($"解析失败: {ex.Message}");
        }
        finally
        {
            _isTranslating = false;
        }
    }

    /// <summary>
    /// 获取选中文本锚点位置（优先 UIA 异步，降级为鼠标位置）
    /// </summary>
    private async Task<System.Windows.Point> GetSelectionAnchorPositionAsync()
    {
        try
        {
            var location = await SelectionLocator.TryGetSelectionBoundsAsync();
            if (location != null && location.IsValid)
                return location.EndPoint;
        }
        catch (Exception ex)
        {
            Logger.Warn("App", $"UIA定位异常: {ex.Message}");
        }

        // 降级为当前鼠标位置（GetCursorPos 返回物理像素，转 DIP）
        Win32Api.GetCursorPos(out var cursorPoint);
        return DpiHelper.PhysicalToLogical(
            new System.Windows.Point(cursorPoint.X, cursorPoint.Y));
    }

    /// <summary>
    /// 红点取消处理（点击其他位置或自动隐藏）
    /// </summary>
    private void OnSelectionCancelled()
    {
        Interlocked.Increment(ref _selectionGeneration);
        _selectionCts?.Cancel();
        _selectionCts = null;
        _selectionDetector!.IsRedDotVisible = false;
        _redDotWindow?.Hide();
        _pendingSelection = null;
    }

    // ==================== 第三期：托盘 + 设置 ====================

    /// <summary>
    /// 打开设置窗口
    /// </summary>
    private void OnSettingsRequested()
    {
        Dispatcher.Invoke(() =>
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_settings!, OnSettingsSaved);
            _settingsWindow.Closed += (s, e) => _settingsWindow = null;
            _settingsWindow.Show();
        });
    }

    /// <summary>
    /// 设置保存回调 - 更新翻译服务和状态
    /// </summary>
    private void OnSettingsSaved(AppSettings settings)
    {
        _translationService?.UpdateSettings(settings);

        // 更新快捷键配置（后台线程执行，避免钩子 Stop/Start 阻塞 UI）
        if (_keyboardHook != null)
        {
            Task.Run(() =>
            {
                _keyboardHook.Stop();
                _keyboardHook.HotKey = settings.HotKeyVK;
                _keyboardHook.RequireAlt = settings.HotKeyRequireAlt;
                _keyboardHook.RequireCtrl = settings.HotKeyRequireCtrl;
                _keyboardHook.RequireShift = settings.HotKeyRequireShift;
                if (settings.HotKeyEnabled)
                {
                    _keyboardHook.Start();
                }
            });
        }

        // 同步托盘菜单快捷键开关状态
        _trayIcon?.SetHotKeyEnabled(settings.HotKeyEnabled);

        UpdateTrayToolTip();
    }

    /// <summary>
    /// 打开翻译历史窗口
    /// </summary>
    private void OnHistoryRequested()
    {
        Dispatcher.Invoke(() =>
        {
            if (_historyWindow != null)
            {
                _historyWindow.Activate();
                return;
            }

            _historyWindow = new HistoryWindow();
            _historyWindow.Closed += (s, e) => _historyWindow = null;
            _historyWindow.Show();
        });
    }

    /// <summary>
    /// 快捷键开关切换
    /// </summary>
    private void OnHotKeyToggled(bool enabled)
    {
        _settings!.HotKeyEnabled = enabled;
        ConfigManager.Save(_settings);

        if (_keyboardHook != null)
        {
            if (enabled)
            {
                _keyboardHook.Start();
                Logger.Info("App", "快捷键已启用");
            }
            else
            {
                _keyboardHook.Stop();
                Logger.Info("App", "快捷键已禁用");
            }
        }
    }

    /// <summary>
    /// 暂停/恢复翻译
    /// </summary>
    private void OnPauseToggled(bool isPaused)
    {
        _settings!.TranslationEnabled = !isPaused;
        ConfigManager.Save(_settings);
        UpdateTrayToolTip();
    }

    /// <summary>
    /// 退出应用
    /// </summary>
    private void OnExitRequested()
    {
        Shutdown();
    }

    /// <summary>
    /// 更新托盘提示文本（显示当前状态）
    /// </summary>
    private void UpdateTrayToolTip()
    {
        if (_trayIcon == null || _settings == null) return;
        var status = _settings.TranslationEnabled ? "翻译已启用" : "翻译已暂停";
        _trayIcon.UpdateToolTip($"QuickTranslate - {status}");
    }

    /// <summary>
    /// 保存翻译历史记录
    /// </summary>
    private void SaveTranslationHistory(string sourceText, string translation, string targetLang, ContentType contentType)
    {
        if (_dbContext == null || string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(translation))
            return;

        try
        {
            var record = new TranslationRecord
            {
                SourceText = sourceText.Trim(),
                Translation = translation.Trim(),
                SourceLanguage = "auto",
                TargetLanguage = targetLang,
                ModelName = _settings.ModelName,
                ContentType = contentType.ToString(),
                TranslatedAt = DateTime.Now
            };

            _dbContext.TranslationRecords.Add(record);
            _dbContext.SaveChanges();
            Logger.Debug("App", $"翻译历史已保存: {record.Id}");
        }
        catch (Exception ex)
        {
            Logger.Error("App", "保存翻译历史失败", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Interlocked.Increment(ref _selectionGeneration);
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = null;
        // 停止看门狗
        _watchdogTimer?.Dispose();

        // 清理资源
        _keyboardHook?.Dispose();
        _selectionDetector?.Dispose();
        _trayIcon?.Dispose();
        _dbContext?.Dispose();

        // 释放单实例 Mutex
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }

        Logger.Info("App", "应用退出");
        Logger.Shutdown();
        base.OnExit(e);
    }
}

