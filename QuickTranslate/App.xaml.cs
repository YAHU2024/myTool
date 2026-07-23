using System;
using System.Diagnostics;
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
    private LogViewerWindow? _logViewerWindow;
    private TranslationDbContext? _dbContext;
    private readonly LatestRequestCoordinator _translationRequests = new();
    private readonly FloatingResultSessionCoordinator _resultSessions = new();
    private readonly TranslationCacheService _translationCache = new();
    private readonly TranslationMetrics _translationMetrics = new();
    private long _selectionGeneration;
    private CancellationTokenSource? _selectionCts;
    private ForegroundWindowInfo? _pendingSelection;
    private FloatingWindowAnchor? _pendingFloatingAnchor;
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
            retentionDays: _settings.LogRetentionDays,
            maxTotalBytes: _settings.LogMaxTotalBytes);
        Logger.Info("App", "app.started", new { os = Environment.OSVersion.ToString(), dotnet = Environment.Version.ToString() });

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
            var exception = args.ExceptionObject as Exception;
            Logger.Fatal("App", "app.unhandled_exception", new
            {
                thread = "AppDomain",
                error_type = exception?.GetType().Name ?? args.ExceptionObject.GetType().Name
            }, exception);
        };

        // 初始化数据库
        _dbContext = new TranslationDbContext();
        _dbContext.EnsureDatabaseCreated();

        // 初始化翻译服务
        _translationService = new OpenAITranslationService(_settings);

        // 初始化悬浮窗（单例复用）
        _floatingWindow = new FloatingWindow();
        _floatingWindow.ModeRequested += OnModeRequested;
        _floatingWindow.RefreshRequested += OnRefreshRequested;
        _floatingWindow.HideRequested += OnHideRequested;
        _floatingWindow.ScrollStateChanged += OnScrollStateChanged;

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
        _trayIcon.RestoreRequested += OnRestoreRequested;
        _trayIcon.HistoryRequested += OnHistoryRequested;
        _trayIcon.LogsRequested += OnLogsRequested;
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
        if (_translationService == null || _settings == null || _floatingWindow == null)
            return;

        FloatingWindowAnchor? floatingAnchor = null;

        var sourceWindow = TerminalDetector.CaptureForegroundWindow();

        // 浏览器中禁用翻译：避免与浏览器翻译插件冲突
        if (!_settings.EnableInBrowser && BrowserDetector.IsForegroundBrowser(_settings.CustomBrowserProcesses))
        {
            Logger.Debug("App", "热键触发但前台为浏览器，已跳过（浏览器翻译已禁用）");
            return;
        }

        try
        {
            if (!TerminalDetector.TryCreateCopyRequest(sourceWindow, _settings, out var copyRequest, out var rejectionMessage))
            {
                floatingAnchor = CreateFloatingAnchor(await GetSelectionLocationAsync());
                await ShowMessageWithoutReplacingSessionAsync(
                    rejectionMessage ?? "无法安全获取选中文本",
                    floatingAnchor.Value);
                return;
            }

            // Copy before the optional UIA lookup so focus cannot change during an async operation.
            var selectedText = await ClipboardHelper.GetSelectedTextAsync(copyRequest!);
            floatingAnchor = CreateFloatingAnchor(await GetSelectionLocationAsync());

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                await ShowMessageWithoutReplacingSessionAsync(
                    "请先选中要翻译的文本",
                    floatingAnchor.Value);
                return;
            }

            // 智能内容检测（仅在 SmartContentType 开启时执行）—— 提前到翻译前
            var detection = _settings.SmartContentType
                ? ContentTypeDetector.DetectDetailed(selectedText)
                : null;
            var contentType = detection?.ContentType ?? ContentType.Translation;

            await StartSessionRequestAsync(
                selectedText,
                contentType,
                floatingAnchor.Value,
                "热键翻译",
                detection);
        }
        catch (Exception ex)
        {
            Logger.Error("App", "热键翻译出错", ex);
            floatingAnchor ??= CreateCursorFloatingAnchor();
            await ShowMessageWithoutReplacingSessionAsync(
                $"翻译失败: {ex.Message}",
                floatingAnchor.Value);
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
            _pendingFloatingAnchor = CreateFloatingAnchor(
                location,
                _redDotWindow.DotScreenPosition);
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
        if (_translationService == null || _settings == null || _floatingWindow == null)
            return;

        if (_pendingSelection == null) return;

        // 标记红点已隐藏
        _selectionDetector!.IsRedDotVisible = false;
        _redDotWindow?.Hide();

        var sourceWindow = _pendingSelection;
        _pendingSelection = null;
        var floatingAnchor = _pendingFloatingAnchor;
        _pendingFloatingAnchor = null;
        if (floatingAnchor == null)
            return;

        try
        {
            if (!TerminalDetector.TryCreateCopyRequest(sourceWindow, _settings, out var copyRequest, out var rejectionMessage))
            {
                await ShowMessageWithoutReplacingSessionAsync(
                    rejectionMessage ?? "无法安全获取选中文本",
                    floatingAnchor.Value);
                return;
            }

            var textToTranslate = await ClipboardHelper.GetSelectedTextAsync(copyRequest!);
            if (string.IsNullOrWhiteSpace(textToTranslate))
            {
                // A red dot can be created by a double-click on empty space. Keep
                // that accidental hover completely silent and unobtrusive.
                return;
            }

            // 智能内容检测（仅在 SmartContentType 开启时执行）—— 提前到翻译前
            var detection = _settings.SmartContentType
                ? ContentTypeDetector.DetectDetailed(textToTranslate)
                : null;
            var contentType = detection?.ContentType ?? ContentType.Translation;

            await StartSessionRequestAsync(
                textToTranslate,
                contentType,
                floatingAnchor.Value,
                "红点翻译",
                detection);
        }
        catch (Exception ex)
        {
            Logger.Error("App", "红点翻译出错", ex);
            await ShowMessageWithoutReplacingSessionAsync(
                $"翻译失败: {ex.Message}",
                floatingAnchor.Value);
        }
    }

    private async void OnModeRequested(ContentType mode)
    {
        if (_floatingWindow is null)
            return;

        await ExecuteSessionTransitionAsync(_resultSessions.SwitchMode(mode), "模式切换");
    }

    private async void OnRefreshRequested()
    {
        if (_floatingWindow is null)
            return;

        await ExecuteSessionTransitionAsync(
            _resultSessions.RefreshMode(),
            "重新生成",
            TranslationCacheReadMode.BypassCache);
    }

    private void OnHideRequested()
    {
        _floatingWindow?.ResetPin();
        _floatingWindow?.Hide();
    }

    private void OnScrollStateChanged(
        Guid sessionId,
        ContentType mode,
        double scrollOffset,
        bool autoScrollEnabled)
    {
        _resultSessions.TrySetScrollState(
            sessionId,
            mode,
            scrollOffset,
            autoScrollEnabled);
    }

    private Task StartSessionRequestAsync(
        string text,
        ContentType contentType,
        FloatingWindowAnchor floatingAnchor,
        string operationName,
        DetectionResult? detection = null)
    {
        var transition = _resultSessions.StartSession(text, floatingAnchor, contentType, detection);
        return ExecuteSessionTransitionAsync(transition, operationName);
    }

    private async Task ExecuteSessionTransitionAsync(
        FloatingResultSessionTransition transition,
        string operationName,
        TranslationCacheReadMode cacheReadMode = TranslationCacheReadMode.UseCache)
    {
        if (_floatingWindow is null || transition.Session is null)
            return;

        if (transition.Kind == FloatingResultSessionTransitionKind.RestoredCompleted)
        {
            _translationRequests.Cancel();
            var state = transition.Session.ModeStates[transition.Session.ActiveMode];
            var presentationId = _floatingWindow.BeginReplacement(_resultSessions.CurrentPresentationId);
            await ShowRequestResultAsync(
                CreateDisplayRequest(transition.Session.SourceText, transition.Session.ActiveMode),
                state.RawText,
                transition.Session.Anchor ?? _floatingWindow.CurrentAnchor,
                presentationId);
            _floatingWindow.SetSessionView(
                transition.Session.SessionId,
                transition.Session.ActiveMode,
                state);
            return;
        }

        if (transition.Kind != FloatingResultSessionTransitionKind.StartedRequest ||
            transition.RequestIdentity is not { } identity ||
            transition.Session.Anchor is not { } anchor)
        {
            return;
        }

        var visualPresentationId = _floatingWindow.BeginReplacement(identity.PresentationId);
        _floatingWindow.SetSessionView(
            transition.Session.SessionId,
            transition.Session.ActiveMode,
            transition.Session.ModeStates[transition.Session.ActiveMode]);
        await ExecuteRequestAsync(
            transition.Session.SourceText,
            identity.Mode,
            anchor,
            visualPresentationId,
            identity,
            identity.Mode == ContentType.Analysis
                ? TranslationRequestKind.Analysis
                : TranslationRequestKind.Translation,
            operationName,
            cacheReadMode);
    }

    private TranslationRequest CreateDisplayRequest(string text, ContentType contentType)
    {
        return _translationService!.CreateRequest(
            text,
            _settings!.TargetLanguage,
            contentType,
            contentType == ContentType.Analysis
                ? TranslationRequestKind.Analysis
                : TranslationRequestKind.Translation);
    }

    private async Task ExecuteRequestAsync(
        string text,
        ContentType contentType,
        FloatingWindowAnchor floatingAnchor,
        long presentationId,
        FloatingResultRequestIdentity sessionIdentity,
        TranslationRequestKind kind,
        string operationName,
        TranslationCacheReadMode cacheReadMode)
    {
        if (_translationService == null || _settings == null || _floatingWindow == null)
            return;

        // CreateRequest snapshots all settings that can affect this request and its cache key.
        var request = _translationService.CreateRequest(
            text,
            _settings.TargetLanguage,
            contentType,
            kind);
        var requestScope = BeginTranslationRequest();
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            requestScope.Token.ThrowIfCancellationRequested();

            if (_translationCache.TryGet(request, cacheReadMode, out var cachedResult))
            {
                if (!IsCurrentRequest(requestScope))
                {
                    _translationMetrics.RecordExpired();
                    return;
                }
                if (!_resultSessions.TryComplete(sessionIdentity, cachedResult))
                {
                    _translationMetrics.RecordExpired();
                    return;
                }

                await ShowRequestResultAsync(
                    request,
                    cachedResult,
                    floatingAnchor,
                    presentationId);
                UpdateFloatingSessionView();
                SaveTranslationHistory(
                    request.Text,
                    cachedResult,
                    request.TargetLanguage,
                    request.ContentType,
                    request.ModelName);
                _translationMetrics.RecordCompleted(TimeSpan.Zero, cacheHit: true);
                Logger.Info("App", "translation.cache_hit", new
                {
                    operation = operationName,
                    content_type = request.ContentType.ToString(),
                    result_len = cachedResult.Length
                });
                return;
            }

            var shown = await ShowRequestLoadingAsync(
                request,
                floatingAnchor,
                presentationId);
            if (!shown)
            {
                _resultSessions.TryCancel(sessionIdentity);
                return;
            }

            var result = await _translationService.ExecuteStreamingAsync(
                request,
                chunk => Dispatcher.BeginInvoke(() =>
                {
                    if (IsCurrentRequest(requestScope) &&
                        _resultSessions.TryUpdateStreaming(sessionIdentity, chunk) &&
                        _floatingWindow?.IsPresentationCurrent(presentationId) == true)
                    {
                        _floatingWindow.UpdateTranslation(presentationId, chunk);
                    }
                }),
                requestScope.Token);

            requestScope.Token.ThrowIfCancellationRequested();
            if (!IsCurrentRequest(requestScope))
            {
                _translationMetrics.RecordExpired();
                return;
            }
            if (!_resultSessions.TryComplete(sessionIdentity, result))
            {
                _translationMetrics.RecordExpired();
                return;
            }

            UpdateFloatingSessionView();
            _translationCache.Set(request, result);
            SaveTranslationHistory(
                request.Text,
                result,
                request.TargetLanguage,
                request.ContentType,
                request.ModelName);
            var duration = Stopwatch.GetElapsedTime(startedAt);
            _translationMetrics.RecordCompleted(duration);
            Logger.Info("App", "translation.presented", new
            {
                operation = operationName,
                content_type = request.ContentType.ToString(),
                result_len = result.Length,
                duration_ms = duration.TotalMilliseconds
            });
        }
        catch (OperationCanceledException) when (requestScope.Token.IsCancellationRequested || !IsCurrentRequest(requestScope))
        {
            _resultSessions.TryCancel(sessionIdentity);
            _translationMetrics.RecordCancelled();
            Logger.Debug("App", "translation.cancelled", new { operation = operationName, request_id = requestScope.RequestId });
        }
        catch (Exception ex)
        {
            if (IsCurrentRequest(requestScope))
                _translationMetrics.RecordFailed();
            else
                _translationMetrics.RecordExpired();
            Logger.Error("App", "translation.failed", new
            {
                operation = operationName,
                request_id = requestScope.RequestId,
                error_type = ex.GetType().Name
            }, ex);
            if (IsCurrentRequest(requestScope) &&
                _resultSessions.TryFail(sessionIdentity, ex.Message) &&
                _floatingWindow.IsPresentationCurrent(presentationId))
            {
                UpdateFloatingSessionView();
                _floatingWindow.UpdateTranslation(
                    presentationId,
                    $"{operationName}失败: {ex.Message}");
            }
        }
        finally
        {
            CompleteTranslationRequest(requestScope);
        }
    }

    private void UpdateFloatingSessionView()
    {
        if (_floatingWindow is null || _resultSessions.CurrentSession is not { } session)
            return;

        _floatingWindow.SetSessionView(
            session.SessionId,
            session.ActiveMode,
            session.ModeStates[session.ActiveMode]);
    }

    private async Task ShowMessageWithoutReplacingSessionAsync(
        string message,
        FloatingWindowAnchor anchor)
    {
        if (_floatingWindow is null || _resultSessions.CurrentSession is not null)
            return;

        var presentationId = _floatingWindow.BeginReplacement();
        await _floatingWindow.ShowTranslationAsync(
            presentationId,
            message,
            anchor,
            ContentType.Translation);
    }

    private LatestRequestCoordinator.RequestScope BeginTranslationRequest() =>
        _translationRequests.Begin();

    private bool IsCurrentRequest(LatestRequestCoordinator.RequestScope requestScope) =>
        _translationRequests.IsCurrent(requestScope);

    private void CompleteTranslationRequest(LatestRequestCoordinator.RequestScope requestScope) =>
        _translationRequests.Complete(requestScope);

    private void CancelActiveTranslationRequest()
    {
        _resultSessions.CancelActiveRequest();
        _translationRequests.Cancel();
    }

    private Task<bool> ShowRequestLoadingAsync(
        TranslationRequest request,
        FloatingWindowAnchor floatingAnchor,
        long presentationId)
    {
        _floatingWindow!.SetLoading(true);
        var loadingText = request.Kind == TranslationRequestKind.Analysis
            ? "解析中..."
            : "翻译中...";
        return _floatingWindow!.ShowTranslationAsync(
            presentationId,
            loadingText,
            floatingAnchor,
            request.ContentType,
            request.FallbackUsed ? request.Text : null);
    }

    private Task<bool> ShowRequestResultAsync(
        TranslationRequest request,
        string result,
        FloatingWindowAnchor floatingAnchor,
        long presentationId)
    {
        _floatingWindow!.SetLoading(false);
        return _floatingWindow!.ShowTranslationAsync(
            presentationId,
            result,
            floatingAnchor,
            request.ContentType,
            request.FallbackUsed ? request.Text : null);
    }


    /// <summary>
    /// 获取选中文本锚点位置（优先 UIA 异步，降级为鼠标位置）
    /// </summary>
    private async Task<SelectionLocation> GetSelectionLocationAsync()
    {
        try
        {
            var location = await SelectionLocator.TryGetSelectionBoundsAsync();
            if (location != null && location.IsValid)
                return location;
        }
        catch (Exception ex)
        {
            Logger.Warn("App", "selection.location_failed", new { error_type = ex.GetType().Name });
        }

        // Fallback stays in physical screen pixels, matching UIA and RedDotWindow.
        Win32Api.GetCursorPos(out var cursorPoint);
        var fallbackPoint = new System.Windows.Point(cursorPoint.X, cursorPoint.Y);
        return new SelectionLocation
        {
            IsValid = false,
            FallbackPoint = fallbackPoint
        };
    }

    private static FloatingWindowAnchor CreateFloatingAnchor(
        SelectionLocation location,
        Point? preferredPoint = null)
    {
        var point = preferredPoint ?? (location.IsValid
            ? location.EndPoint
            : location.FallbackPoint);
        var bounds = location.IsValid ? location.Bounds : Rect.Empty;
        return new FloatingWindowAnchor(point, bounds);
    }

    private static FloatingWindowAnchor CreateCursorFloatingAnchor()
    {
        Win32Api.GetCursorPos(out var cursorPoint);
        return new FloatingWindowAnchor(
            new Point(cursorPoint.X, cursorPoint.Y),
            Rect.Empty);
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
        _pendingFloatingAnchor = null;
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
        CancelActiveTranslationRequest();
        _translationCache.Clear();
        _settings = settings;
        _translationService?.UpdateSettings(settings);
        Logger.Configure(
            Logger.ParseLevel(settings.LogLevel),
            settings.LogRetentionDays,
            settings.LogMaxTotalBytes);

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

    private void OnLogsRequested()
    {
        Dispatcher.Invoke(() =>
        {
            if (_logViewerWindow != null)
            {
                _logViewerWindow.Activate();
                return;
            }

            _logViewerWindow = new LogViewerWindow(_translationMetrics, _translationCache);
            _logViewerWindow.Closed += (_, _) => _logViewerWindow = null;
            _logViewerWindow.Show();
        });
    }

    private void OnRestoreRequested()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_floatingWindow is null || _resultSessions.CurrentSession is null)
                return;

            _floatingWindow.ShowExistingResult();
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
        if (isPaused)
            CancelActiveTranslationRequest();
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
    private void SaveTranslationHistory(
        string sourceText,
        string translation,
        string targetLang,
        ContentType contentType,
        string modelName)
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
                ModelName = modelName,
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
        CancelActiveTranslationRequest();
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
        _translationService?.Dispose();
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

