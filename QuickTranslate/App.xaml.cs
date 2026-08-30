using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
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
    private const int MaxConcurrentScreenshotTranslations = 3;
    private sealed record PendingSelectionCapture(
        ForegroundWindowInfo SourceWindow,
        SelectionIntent Intent,
        SelectionEvidenceKind Evidence,
        FloatingWindowAnchor Anchor,
        long Generation);

    private sealed record ScreenshotUiState(
        bool FloatingWindowVisible,
        bool QuickLookupWindowVisible);

    private sealed record ModelSettingsContext(Guid SessionId, ContentType Mode);

    private GlobalKeyboardHook? _keyboardHook;
    private GlobalKeyboardHook? _quickLookupHook;
    private SelectionDetector? _selectionDetector;
    private OpenAITranslationService? _translationService;
    private ITtsService? _ttsService;
    private TtsPlaybackCoordinator? _ttsPlayback;
    private IWordLookupService? _wordLookupService;
    private OpenAIWordLookupService? _openAiWordLookupService;
    private LocalDictionaryWordLookupService? _localWordLookupService;
    private QuickLookupWindow? _quickLookupWindow;
    private AppSettings? _settings;
    private FloatingWindow? _floatingWindow;
    private RedDotWindow? _redDotWindow;
    private ScreenshotSelectionWindow? _screenshotWindow;
    private ScreenshotTranslationOverlayWindow? _screenshotOverlayWindow;
    private ScreenshotTranslationProgressWindow? _screenshotProgressWindow;
    private IOcrService? _screenshotOcrService;
    private ScreenshotTranslationCoordinator? _screenshotTranslationCoordinator;
    private CancellationTokenSource? _screenshotTranslationCts;
    private TrayIconManager? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private ModelSettingsContext? _modelSettingsContext;
    private HistoryWindow? _historyWindow;
    private LogViewerWindow? _logViewerWindow;
    private FeedbackWindow? _feedbackWindow;
    private CrashRecoveryPromptWindow? _crashRecoveryPromptWindow;
    private CrashRecoveryTracker? _crashRecoveryTracker;
    private RecoveryEvent? _pendingRecoveryEvent;
    private TranslationDbContext? _dbContext;
    private readonly LatestRequestCoordinator _translationRequests = new();
    private readonly FloatingResultSessionCoordinator _resultSessions = new();
    private readonly ModelSelectionCoordinator _modelSelection = new();
    private readonly TranslationCacheService _translationCache = new();
    private readonly TranslationMetrics _translationMetrics = new();
    private readonly IScreenshotCaptureService _screenshotCaptureService = new GdiScreenshotCaptureService();
    private readonly WordLookupSessionCoordinator _lookupSessions = new();
    private readonly RecentLookupBuffer _recentLookups = new();
    private readonly TrayClickCoordinator _trayClicks = new();
    private long _pendingTrayClickSequence;
    private int _lookupVisible;
    private readonly DispatcherTimer _lookupDeactivationTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(100)
    };
    private long _selectionGeneration;
    private CancellationTokenSource? _selectionCts;
    private PendingSelectionCapture? _pendingSelection;
    private Mutex? _singleInstanceMutex;
    private Window? _hiddenWindow; // 隐藏主窗口，稳定 WPF Application 生命周期
    private Timer? _watchdogTimer; // 看门狗线程，定期写入状态文件
    private bool _isExiting;

    // 控制台信号处理
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);
    private delegate bool ConsoleCtrlHandler(uint ctrlType);
    private static ConsoleCtrlHandler? _ctrlHandler; // 防止 GC 回收

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 初始化日志系统
#if DEBUG
        // 附加控制台以实时输出日志（接受信号风险，但有 CtrlHandler 保护）
        Win32Api.AttachConsole(Win32Api.ATTACH_PARENT_PROCESS);
        // 注册控制台信号处理器，忽略 Ctrl+C/Ctrl+Break/关闭信号
        _ctrlHandler = ConsoleCtrlCallback;
        SetConsoleCtrlHandler(_ctrlHandler, true);
#endif
        Logger.Init();

        // 加载配置。Logger must already be available so config failures are recorded safely.
        _settings = ConfigManager.Load();
        var configLoadStatus = ConfigManager.LastLoadStatus;
        Logger.Configure(
            Logger.ParseLevel(_settings.LogLevel),
            _settings.LogRetentionDays,
            _settings.LogMaxTotalBytes);
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

        _crashRecoveryTracker = new CrashRecoveryTracker();
        _pendingRecoveryEvent = _crashRecoveryTracker.StartRun(
            typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
            RuntimeInformation.OSArchitecture.ToString(),
            DateTimeOffset.Now);

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
        _screenshotOcrService = ScreenshotOcrServiceFactory.Create();
        _screenshotTranslationCoordinator = new ScreenshotTranslationCoordinator(_screenshotOcrService);
        var screenshotOcrCapability = _screenshotOcrService.Probe();
        Logger.Info("Screenshot", "screenshot.ocr_engine_selected", new
        {
            engine = screenshotOcrCapability.EngineId,
            available = screenshotOcrCapability.IsAvailable,
            supports_polygons = screenshotOcrCapability.SupportsPolygons,
            supports_confidence = screenshotOcrCapability.SupportsConfidence,
            language_count = screenshotOcrCapability.SupportedLanguageTags.Count
        });

        // 初始化悬浮窗（单例复用）
        _floatingWindow = new FloatingWindow();
        _floatingWindow.ModeRequested += OnModeRequested;
        _floatingWindow.RefreshRequested += OnRefreshRequested;
        _floatingWindow.HideRequested += OnHideRequested;
        _floatingWindow.ScrollStateChanged += OnScrollStateChanged;
        _floatingWindow.AnalysisFollowUpRequested += OnAnalysisFollowUpRequested;
        _floatingWindow.AnalysisFollowUpReplaceRequested += OnAnalysisFollowUpReplaceRequested;
        _floatingWindow.AnalysisFollowUpStopRequested += OnAnalysisFollowUpStopRequested;
        _floatingWindow.AnalysisFollowUpRetryRequested += OnAnalysisFollowUpRetryRequested;
        _floatingWindow.AnalysisDraftChanged += OnAnalysisDraftChanged;
        _floatingWindow.ModelProfileSelected += OnModelProfileSelected;
        _floatingWindow.ModelSettingsRequested += OnModelSettingsRequested;
        _floatingWindow.TranslationDirectionToggleRequested += OnTranslationDirectionToggleRequested;

        _ttsService = new EdgeTtsService();
        _ttsPlayback = new TtsPlaybackCoordinator(_ttsService);
        _floatingWindow.AttachTts(_ttsPlayback);
        _floatingWindow.ApplyTtsSettings(
            _settings.TtsEnabled,
            _settings.TtsVoice,
            _settings.TtsRate,
            _settings.TtsMaxChars);

        _openAiWordLookupService = new OpenAIWordLookupService(CreateWordLookupSettings(_settings));
        _localWordLookupService = TryCreateLocalDictionaryWordLookupService();
        _wordLookupService = _localWordLookupService is null
            ? _openAiWordLookupService
            : new CompositeWordLookupService(_localWordLookupService, _openAiWordLookupService);
        _quickLookupWindow = new QuickLookupWindow(
            _wordLookupService,
            _openAiWordLookupService,
            _lookupSessions,
            _recentLookups,
            _ttsPlayback,
            _settings.TargetLanguage);
        _quickLookupWindow.ApplySettings(
            _settings.TargetLanguage,
            _settings.TtsEnabled,
            _settings.TtsVoice,
            _settings.TtsRate,
            _settings.TtsMaxChars);
        _quickLookupWindow.DeactivationRequested += OnLookupDeactivated;
        _quickLookupWindow.HideRequested += () => Volatile.Write(ref _lookupVisible, 0);
        _lookupDeactivationTimer.Tick += (_, _) =>
        {
            _lookupDeactivationTimer.Stop();
            ApplyTrayClickAction(_trayClicks.ConfirmDeactivation());
        };

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
        if (CanTriggerHotKey)
        {
            _keyboardHook.Start();
        }

        // 启动快速查词全局键盘钩子（仅当用户开启时）
        _quickLookupHook = new GlobalKeyboardHook();
        _quickLookupHook.HotKey = _settings.QuickLookupHotKeyVK;
        _quickLookupHook.RequireAlt = _settings.QuickLookupHotKeyRequireAlt;
        _quickLookupHook.RequireCtrl = _settings.QuickLookupHotKeyRequireCtrl;
        _quickLookupHook.RequireShift = _settings.QuickLookupHotKeyRequireShift;
        _quickLookupHook.HotKeyPressed += OnQuickLookupHotKeyPressed;
        if (_settings.QuickLookupHotKeyEnabled)
        {
            _quickLookupHook.Start();
        }

        // 启动文本选择检测器
        _selectionDetector = new SelectionDetector();
        _selectionDetector.SelectionCompleted += OnSelectionCompleted;
        _selectionDetector.ClickedOutside += OnSelectionCancelled;
        _selectionDetector.Start();

        // 初始化系统托盘图标
        _trayIcon = new TrayIconManager();
        _trayIcon.ScreenshotTranslationRequested += OnScreenshotTranslationRequested;
        _trayIcon.SettingsRequested += OnSettingsRequested;
        _trayIcon.RestoreRequested += OnRestoreRequested;
        _trayIcon.LookupClickStarted += OnLookupClickStarted;
        _trayIcon.LookupSingleClickConfirmed += OnLookupSingleClickConfirmed;
        _trayIcon.LookupDoubleClick += OnLookupDoubleClick;
        _trayIcon.HistoryRequested += OnHistoryRequested;
        _trayIcon.FeedbackRequested += () => OnFeedbackRequested(FeedbackMode.Problem);
        _trayIcon.UpdateRequested += OnUpdateRequested;
        _trayIcon.BalloonTipClicked += OnUpdateBalloonClicked;
        _trayIcon.PauseToggled += OnPauseToggled;
        _trayIcon.ExitRequested += OnExitRequested;

        _trayIcon.SetPaused(TranslationTriggerModes.IsPaused(_settings.TranslationTriggerMode));
        _trayIcon.SetRestoreAvailable(false);

        // 根据配置更新托盘提示
        UpdateTrayToolTip();

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
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Visibility = Visibility.Hidden
        };
        _hiddenWindow.Show();

        if (configLoadStatus != ConfigLoadStatus.Loaded)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => ShowStartupConfiguration(configLoadStatus)));
        }
        else if (_pendingRecoveryEvent is { PromptState: RecoveryPromptState.Pending } &&
                 _settings.CrashFeedbackPromptEnabled)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(ShowRecoveryPromptIfPending));
        }

        // 启动时延迟检查更新（不阻塞初始化）
        // 将 Authenticode 验证策略注入 UpdateService
        UpdateService.RequireAuthenticodeSignature = _settings.RequireAuthenticodeSignature;
        if (_settings.CheckForUpdateOnStartup)
        {
            ScheduleStartupUpdateCheck(delaySeconds: 5);
        }
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

    private bool CanTriggerSelection => _settings is null ||
        TranslationTriggerModes.CanTriggerSelection(_settings.TranslationTriggerMode);

    private bool CanTriggerHotKey => _settings is null ||
        TranslationTriggerModes.CanTriggerHotKey(_settings.TranslationTriggerMode);

    /// <summary>
    /// 热键事件处理（默认 Alt+Q）
    /// </summary>
    private async void OnHotKeyPressed()
    {
        if (!CanTriggerHotKey) return;
        if (_translationService == null || _settings == null || _floatingWindow == null)
            return;

        FloatingWindowAnchor? floatingAnchor = null;

        try
        {
            // A selection inside our own result window is an explicit follow-up
            // request. Read it directly from WPF instead of routing it through
            // UIA or simulated Ctrl+C.
            if (_floatingWindow.TryGetSecondarySelection(
                    out var secondaryText,
                    out var secondaryAnchor))
            {
                if (_floatingWindow.IsGenerationBusyForSecondaryRequest)
                {
                    _floatingWindow.ShowSelectionCaptureFeedback("请等待当前生成完成");
                    return;
                }

                var secondaryRoute = TranslationRouteResolver.Resolve(
                    secondaryText,
                    _settings.SmartContentType);
                await StartSessionRequestAsync(
                    secondaryText,
                    secondaryRoute.InitialMode,
                    secondaryAnchor,
                    "悬浮窗二次翻译",
                    secondaryRoute.ContentDecision);
                return;
            }

            var sourceWindow = await TerminalDetector.CaptureForegroundWindowWithFocusAsync();

            // 浏览器中禁用翻译：避免与浏览器翻译插件冲突
            if (!_settings.EnableInBrowser && BrowserDetector.IsForegroundBrowser(_settings.CustomBrowserProcesses))
            {
                Logger.Debug("App", "热键触发但前台为浏览器，已跳过（浏览器翻译已禁用）");
                return;
            }

            var location = await SelectionLocator.TryGetSelectionBoundsAsync(750);
            var evidence = location is { IsValid: true }
                ? SelectionEvidenceKind.UiaTextSelectionBounds
                : SelectionEvidenceKind.None;
            var intent = new SelectionIntent(
                SelectionGestureKind.HotKey,
                default,
                default,
                DateTimeOffset.UtcNow);
            var plan = SelectionCapturePlanner.Create(
                sourceWindow,
                _settings,
                evidence,
                intent);
            TerminalDetector.LogDecision(sourceWindow, _settings, plan.Decision);
            if (!plan.IsAllowed)
            {
                floatingAnchor = CreateFloatingAnchor(await GetSelectionLocationAsync());
                await ShowMessageWithoutReplacingSessionAsync(
                    plan.RejectionMessage ?? "无法安全获取选中文本",
                    floatingAnchor.Value);
                return;
            }

            var selectedText = await ClipboardHelper.GetSelectedTextAsync(plan.Request!);
            floatingAnchor = CreateFloatingAnchor(
                location is { IsValid: true } ? location : await GetSelectionLocationAsync());

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                await ShowMessageWithoutReplacingSessionAsync(
                    "请先选中要翻译的文本",
                    floatingAnchor.Value);
                return;
            }

            var route = TranslationRouteResolver.Resolve(selectedText, _settings.SmartContentType);

            await StartSessionRequestAsync(
                selectedText,
                route.InitialMode,
                floatingAnchor.Value,
                "热键翻译",
                route.ContentDecision);
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
    /// 快速查词热键事件处理（默认 Alt+W）—— 切换快速查词窗口显隐。
    /// </summary>
    private void OnQuickLookupHotKeyPressed()
    {
        if (_quickLookupWindow is null)
            return;

        Dispatcher.Invoke(() =>
        {
            if (Volatile.Read(ref _lookupVisible) == 1)
            {
                // 已可见 → 隐藏
                _quickLookupWindow.HidePanel();
            }
            else
            {
                // 居中显示在当前鼠标所在显示器的工作区
                ShowQuickLookupCentered();
            }
        });
    }

    private void OnScreenshotTranslationRequested()
    {
        Dispatcher.BeginInvoke(StartScreenshotCapture);
    }

    /// <summary>
    /// 从托盘进入单显示器截图框选。选区和捕获均使用物理像素，窗口只负责绘制遮罩。
    /// </summary>
    private void StartScreenshotCapture()
    {
        if (_isExiting || _screenshotWindow is not null || _screenshotOverlayWindow is not null ||
            _screenshotProgressWindow is not null ||
            _screenshotTranslationCts is not null)
            return;
        if (!Win32Api.GetCursorPos(out var cursor))
        {
            _trayIcon?.ShowBalloonTip("截图翻译", "无法读取当前鼠标位置，请重试。", System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }

        var monitor = Win32Api.GetPhysicalMonitorAreaAtPoint(new Point(cursor.X, cursor.Y));
        if (monitor.IsEmpty)
        {
            _trayIcon?.ShowBalloonTip("截图翻译", "无法确定当前显示器，请重试。", System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }

        var monitorRegion = new ScreenshotRegion(
            checked((int)monitor.Left),
            checked((int)monitor.Top),
            checked((int)monitor.Width),
            checked((int)monitor.Height));
        var restoreState = new ScreenshotUiState(
            _floatingWindow?.IsVisible == true,
            _quickLookupWindow?.IsVisible == true);

        // Cancel any pending text-selection red dot before taking control of the mouse.
        OnSelectionCancelled();
        if (restoreState.FloatingWindowVisible)
            _floatingWindow?.Hide();
        if (restoreState.QuickLookupWindowVisible)
            _quickLookupWindow?.Hide();

        ScreenshotSelectionWindow? window = null;
        try
        {
            window = new ScreenshotSelectionWindow(monitorRegion);
            _screenshotWindow = window;
            window.SelectionCompleted += region =>
                OnScreenshotSelectionCompleted(window, restoreState, region);
            window.Cancelled += () =>
                OnScreenshotSelectionCancelled(window, restoreState);
            window.ShowSelection();
            Logger.Info("Screenshot", "screenshot.selection_started", new
            {
                monitor_width = monitorRegion.Width,
                monitor_height = monitorRegion.Height
            });
        }
        catch (Exception ex)
        {
            window?.CancelSelection();
            Logger.Warn("Screenshot", "screenshot.selection_start_failed", new
            {
                exception_type = ex.GetType().Name
            });
            RestoreScreenshotUi(restoreState);
            _screenshotWindow = null;
            _trayIcon?.ShowBalloonTip("截图翻译", "无法启动截图框选，请重试。", System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    private void OnScreenshotSelectionCancelled(
        ScreenshotSelectionWindow window,
        ScreenshotUiState restoreState)
    {
        if (ReferenceEquals(_screenshotWindow, window))
            _screenshotWindow = null;
        Logger.Info("Screenshot", "screenshot.selection_cancelled", new { });
        RestoreScreenshotUi(restoreState);
    }

    private async void OnScreenshotSelectionCompleted(
        ScreenshotSelectionWindow window,
        ScreenshotUiState restoreState,
        ScreenshotRegion region)
    {
        var pipelineWatch = Stopwatch.StartNew();
        var captureElapsed = TimeSpan.Zero;
        var overlayLayoutElapsed = TimeSpan.Zero;
        var stage = "capture";
        var pipelineStatus = "not_started";
        var pipelineTimings = ScreenshotTranslationStageTimings.Empty;
        var translationRequestCount = 0;
        var overlayItemCount = 0;
        var overlayPlacedCount = 0;
        var overlayDegradedCount = 0;
        var overlaySkippedCount = 0;
        string? selectedOcrEngine = null;
        string? failureType = null;

        // Let the hidden overlay leave the compositor before copying screen pixels.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (_isExiting || _screenshotTranslationCts is not null)
            return;

        using var translationCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _screenshotTranslationCts = translationCts;
        var cancellationToken = translationCts.Token;
        try
        {
            var captureWatch = Stopwatch.StartNew();
            OcrImage image;
            try
            {
                image = await Task.Run(() => _screenshotCaptureService.Capture(region));
            }
            finally
            {
                captureElapsed = captureWatch.Elapsed;
            }
            Logger.Info("Screenshot", "screenshot.capture_completed", new
            {
                width = image.PixelWidth,
                height = image.PixelHeight,
                stride = image.Stride,
                payload_bytes = image.BgraPixels.Length
            });
            await Dispatcher.InvokeAsync(() =>
            {
                if (_isExiting || cancellationToken.IsCancellationRequested)
                    return;
                var progress = new ScreenshotTranslationProgressWindow(region);
                _screenshotProgressWindow = progress;
                progress.CancelRequested += () => _screenshotTranslationCts?.Cancel();
                progress.ShowProgress();
            }, DispatcherPriority.ApplicationIdle);
            var ocrService = _screenshotOcrService;
            var coordinator = _screenshotTranslationCoordinator;
            var translationService = _translationService;
            var settings = _settings;
            if (ocrService is null || coordinator is null || translationService is null || settings is null)
                throw new InvalidOperationException("截图翻译服务尚未初始化。");

            var capability = ocrService.Probe();
            selectedOcrEngine = capability.EngineId;
            if (!capability.IsAvailable)
                throw new OcrEngineUnavailableException(capability.UnavailableReason);
            _trayIcon?.ShowBalloonTip(
                "截图翻译",
                "正在本地识别并翻译，请稍候…",
                System.Windows.Forms.ToolTipIcon.Info);

            stage = "ocr";
            pipelineStatus = "running";
            var pipeline = await coordinator.ExecuteAsync(
                image,
                async (units, token) =>
                {
                    stage = "translation";
                    using var gate = new SemaphoreSlim(MaxConcurrentScreenshotTranslations);
                    var tasks = units.Select(async unit =>
                    {
                        await gate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            Interlocked.Increment(ref translationRequestCount);
                            var translation = await translationService.TranslateAsync(
                                unit.SourceText,
                                settings.TargetLanguage,
                                ContentType.Translation,
                                token).ConfigureAwait(false);
                            return new TranslatedTextUnit(unit.UnitId, translation);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    });
                    return await Task.WhenAll(tasks).ConfigureAwait(false);
                },
                new OcrRecognitionOptions(LanguageHint: null, AllowLanguageFallback: true),
                cancellationToken).ConfigureAwait(true);

            pipelineTimings = pipeline.Timings;
            pipelineStatus = pipeline.Status.ToString();

            if (pipeline.Status == ScreenshotTranslationPipelineStatus.NoText)
            {
                _trayIcon?.ShowBalloonTip(
                    "截图翻译",
                    "未识别到可翻译文字。",
                    System.Windows.Forms.ToolTipIcon.Info);
                return;
            }

            if (pipeline.Status != ScreenshotTranslationPipelineStatus.Completed)
                throw new InvalidOperationException("译文无法安全映射回截图区域。");

            stage = "overlay_layout";
            var overlayWatch = Stopwatch.StartNew();
            try
            {
                var overlayItems = pipeline.Units
                    .Zip(pipeline.Mapping.MappedUnits)
                    .Select(static pair => new ScreenshotOverlayItem(
                        pair.First.Bounds,
                        pair.Second.Translation,
                        pair.First.Blocks.Count == 1 ? pair.First.Blocks[0].Polygon : null,
                        pair.First.UnitId,
                        AverageConfidence(pair.First.Blocks)))
                    .ToArray();
                overlayItemCount = overlayItems.Length;
                if (overlayItems.Length == 0)
                {
                    _trayIcon?.ShowBalloonTip(
                        "截图翻译",
                        "未生成可显示的译文。",
                        System.Windows.Forms.ToolTipIcon.Warning);
                    return;
                }

                var overlayPresented = await Dispatcher.InvokeAsync(() =>
                {
                    if (_isExiting || cancellationToken.IsCancellationRequested)
                        return false;
                    var overlay = new ScreenshotTranslationOverlayWindow(region, image, overlayItems);
                    overlayItemCount = overlay.LayoutResult.Items.Count;
                    overlayPlacedCount = overlay.LayoutResult.PlacedCount;
                    overlayDegradedCount = overlay.LayoutResult.DegradedCount;
                    overlaySkippedCount = overlay.LayoutResult.SkippedCount;
                    if (!overlay.HasRenderableItems)
                    {
                        Logger.Info("Screenshot", "screenshot.overlay_skipped", new
                        {
                            item_count = overlay.LayoutResult.Items.Count,
                            skipped_count = overlay.LayoutResult.SkippedCount
                        });
                        _trayIcon?.ShowBalloonTip(
                            "截图翻译",
                            "译文无法在截图范围内完整显示。",
                            System.Windows.Forms.ToolTipIcon.Warning);
                        return false;
                    }

                    _screenshotOverlayWindow = overlay;
                    overlay.Closed += (_, _) =>
                    {
                        if (ReferenceEquals(_screenshotOverlayWindow, overlay))
                            _screenshotOverlayWindow = null;
                        RestoreScreenshotUi(restoreState);
                    };
                    try
                    {
                        overlay.ShowOverlay();
                    }
                    catch
                    {
                        if (ReferenceEquals(_screenshotOverlayWindow, overlay))
                            _screenshotOverlayWindow = null;
                        overlay.Close();
                        throw;
                    }
                    Logger.Info("Screenshot", "screenshot.overlay_presented", new
                    {
                        block_count = pipeline.OcrResult.Blocks.Count,
                        unit_count = pipeline.Units.Count,
                        placed_count = overlay.LayoutResult.PlacedCount,
                        degraded_count = overlay.LayoutResult.DegradedCount,
                        skipped_count = overlay.LayoutResult.SkippedCount,
                        engine = capability.EngineId,
                        used_language = pipeline.OcrResult.UsedLanguageTag
                    });
                    return true;
                }, DispatcherPriority.ApplicationIdle);
                stage = overlayPresented ? "overlay_presented" : "overlay_skipped";
            }
            finally
            {
                overlayLayoutElapsed = overlayWatch.Elapsed;
            }
        }
        catch (OperationCanceledException)
        {
            pipelineStatus = "cancelled";
            failureType = nameof(OperationCanceledException);
            _trayIcon?.ShowBalloonTip(
                "截图翻译",
                "截图翻译已取消。",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            pipelineStatus = "failed";
            failureType = ex.GetType().Name;
            Logger.Warn("Screenshot", "screenshot.capture_failed", new
            {
                exception_type = ex.GetType().Name,
                width = region.Width,
                height = region.Height
            });
            _trayIcon?.ShowBalloonTip(
                "截图翻译",
                ex is OcrEngineUnavailableException
                    ? "本机 OCR 引擎不可用，请安装对应语言包或配置本地 OCR 模型。"
                    : "截图翻译失败，请缩小区域后重试。",
                System.Windows.Forms.ToolTipIcon.Warning);
        }
        finally
        {
            pipelineWatch.Stop();
            Logger.Info("Screenshot", "screenshot.pipeline_completed", new
            {
                status = pipelineStatus,
                terminal_stage = stage,
                failure_type = failureType,
                engine = selectedOcrEngine ?? "unknown",
                capture_elapsed_ms = Math.Round(captureElapsed.TotalMilliseconds, 2),
                ocr_elapsed_ms = Math.Round(pipelineTimings.OcrElapsed.TotalMilliseconds, 2),
                translation_elapsed_ms = Math.Round(pipelineTimings.TranslationElapsed.TotalMilliseconds, 2),
                mapping_elapsed_ms = Math.Round(pipelineTimings.MappingElapsed.TotalMilliseconds, 2),
                overlay_layout_elapsed_ms = Math.Round(overlayLayoutElapsed.TotalMilliseconds, 2),
                overlay_item_count = overlayItemCount,
                overlay_placed_count = overlayPlacedCount,
                overlay_degraded_count = overlayDegradedCount,
                overlay_skipped_count = overlaySkippedCount,
                total_elapsed_ms = Math.Round(pipelineWatch.Elapsed.TotalMilliseconds, 2),
                ocr_block_count = pipelineTimings.OcrBlockCount,
                translation_unit_count = pipelineTimings.TranslationUnitCount,
                translation_request_count = Volatile.Read(ref translationRequestCount),
                cancelled = cancellationToken.IsCancellationRequested
            });
            if (_screenshotProgressWindow is { } progress)
            {
                _screenshotProgressWindow = null;
                progress.Close();
            }
            if (ReferenceEquals(_screenshotWindow, window))
                _screenshotWindow = null;
            _screenshotTranslationCts = null;
            if (_screenshotOverlayWindow is null)
                RestoreScreenshotUi(restoreState);
        }
    }

    private static double? AverageConfidence(IReadOnlyList<OcrTextBlock> blocks)
    {
        var values = blocks
            .Where(static block => block.Confidence is { } confidence && double.IsFinite(confidence))
            .Select(static block => block.Confidence!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private void RestoreScreenshotUi(ScreenshotUiState restoreState)
    {
        if (_isExiting)
            return;
        if (restoreState.FloatingWindowVisible)
            _floatingWindow?.ShowExistingResult();
        if (restoreState.QuickLookupWindowVisible && _quickLookupWindow is { } quickLookup)
            quickLookup.Show();
    }

    /// <summary>
    /// 将快速查词窗口居中显示在当前鼠标所在显示器的工作区。
    /// </summary>
    private void ShowQuickLookupCentered()
    {
        if (_quickLookupWindow is null)
            return;

        Win32Api.GetCursorPos(out var cursorPt);
        var physicalAnchor = new System.Windows.Point(cursorPt.X, cursorPt.Y);
        var work = Win32Api.GetPhysicalWorkAreaAtPoint(physicalAnchor);
        if (work.IsEmpty)
            return;

        var scale = DpiHelper.GetScaleForPhysicalPoint(physicalAnchor);
        var wndWidth = (int)Math.Round(_quickLookupWindow.Width * scale.X);
        var wndHeight = (int)Math.Round(_quickLookupWindow.Height * scale.Y);

        var centerX = (int)(work.Left + (work.Width - wndWidth) / 2);
        var centerY = (int)(work.Top + (work.Height - wndHeight) / 2);

        _quickLookupWindow.PrepareForShow();
        Volatile.Write(ref _lookupVisible, 1);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(_quickLookupWindow).Handle;
        Win32Api.SetWindowPos(hwnd, IntPtr.Zero, centerX, centerY, wndWidth, wndHeight, 0x0004);
    }

    /// <summary>
    /// 文本选择完成事件处理 - 显示红点。
    /// 防重入：如果上一次操作尚未完成，直接丢弃新触发。
    /// </summary>
    private async void OnSelectionCompleted(
        SelectionGestureKind gestureKind,
        System.Windows.Point startPos,
        System.Windows.Point endPos)
    {
        var generation = Interlocked.Increment(ref _selectionGeneration);
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        var cts = new CancellationTokenSource();
        _selectionCts = cts;
        var token = cts.Token;

        try
        {
            if (!CanTriggerSelection) return;
            if (_redDotWindow == null) return;

            // 浏览器中禁用翻译：避免与浏览器翻译插件冲突
            if (_settings != null && !_settings.EnableInBrowser && BrowserDetector.IsForegroundBrowser(_settings.CustomBrowserProcesses))
            {
                Logger.Debug("App", "选词触发但前台为浏览器，已跳过（浏览器翻译已禁用）");
                return;
            }

            var sourceWindow = await TerminalDetector.CaptureForegroundWindowWithFocusAsync(cancellationToken: token);
            if (sourceWindow == null) return;
            if (sourceWindow.ProcessId == Environment.ProcessId) return;

            var intent = new SelectionIntent(gestureKind, startPos, endPos, DateTimeOffset.UtcNow);

            if (_settings != null && TerminalDetector.ShouldSuppressSelection(sourceWindow, _settings))
            {
                Logger.Debug("App", "selection.terminal_capture_suppressed", new
                {
                    process_name = sourceWindow.ProcessName,
                    window_class = sourceWindow.WindowClassName
                });
                return;
            }

            // 尝试 UIA 异步精确定位（不阻塞 UI 线程）
            var location = await SelectionLocator.TryGetSelectionBoundsAsync(
                startPos,
                endPos,
                2000,
                token);
            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _selectionGeneration)) return;
            if (Win32Api.GetForegroundWindow() != sourceWindow.Handle) return;
            var evidence = location is { IsValid: true }
                ? SelectionEvidenceKind.UiaTextSelectionBounds
                : SelectionEvidenceKind.GestureIntent;
            if (_settings != null)
            {
                var plan = SelectionCapturePlanner.Create(
                    sourceWindow,
                    _settings,
                    evidence,
                    intent);
                TerminalDetector.LogDecision(sourceWindow, _settings, plan.Decision);
                if (!plan.IsAllowed)
                {
                    Logger.Debug("App", "selection.capture_plan_rejected", new
                    {
                        process_name = sourceWindow.ProcessName,
                        window_class = sourceWindow.WindowClassName,
                        gesture = intent.GestureKind.ToString(),
                        evidence = evidence.ToString(),
                        decision = plan.Decision.Reason.ToString(),
                        action_risk = plan.Decision.ActionRisk.ToString()
                    });
                    return;
                }
            }
            if (location == null || !location.IsValid)
            {
                // Automatic red-dot activation requires a confirmed text
                // selection. The explicit hotkey path still supports apps
                // that do not expose UIA selection bounds.
                return;
            }
            if (!IsSelectionGestureConsistent(location, intent))
                return;

            // Defer clipboard access until the user deliberately hovers the red dot.
            // 显示红点
            _redDotWindow.ShowAt(location);
            var floatingAnchor = CreateFloatingAnchor(
                location,
                _redDotWindow.DotScreenPosition);
            _pendingSelection = new PendingSelectionCapture(
                sourceWindow,
                intent,
                evidence,
                floatingAnchor,
                generation);
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
        if (!CanTriggerSelection) return;
        if (_translationService == null || _settings == null || _floatingWindow == null)
            return;

        if (_pendingSelection == null) return;

        // 标记红点已隐藏
        _selectionDetector!.IsRedDotVisible = false;
        _redDotWindow?.Hide();

        var pendingCapture = _pendingSelection;
        _pendingSelection = null;
        var floatingAnchor = pendingCapture.Anchor;

        try
        {
            if (Win32Api.GetForegroundWindow() != pendingCapture.SourceWindow.Handle ||
                pendingCapture.Generation != Volatile.Read(ref _selectionGeneration))
                return;

            var plan = SelectionCapturePlanner.Create(
                pendingCapture.SourceWindow,
                _settings,
                pendingCapture.Evidence,
                pendingCapture.Intent);
            TerminalDetector.LogDecision(pendingCapture.SourceWindow, _settings, plan.Decision);
            if (!plan.IsAllowed)
            {
                await ShowMessageWithoutReplacingSessionAsync(
                    plan.RejectionMessage ?? "无法安全获取选中文本",
                    floatingAnchor);
                return;
            }

            var textToTranslate = await ClipboardHelper.GetSelectedTextAsync(plan.Request!);
            if (string.IsNullOrWhiteSpace(textToTranslate))
            {
                // A red dot can be created by a double-click on empty space. Keep
                // that accidental hover completely silent and unobtrusive.
                return;
            }

            var route = TranslationRouteResolver.Resolve(textToTranslate, _settings.SmartContentType);

            await StartSessionRequestAsync(
                textToTranslate,
                route.InitialMode,
                floatingAnchor,
                "红点翻译",
                route.ContentDecision);
        }
        catch (Exception ex)
        {
            Logger.Error("App", "红点翻译出错", ex);
            await ShowMessageWithoutReplacingSessionAsync(
                $"翻译失败: {ex.Message}",
                floatingAnchor);
        }
    }

    private async void OnModeRequested(ContentType mode)
    {
        if (_floatingWindow is null)
            return;
        if (_resultSessions.CurrentSession?.ActiveMode == mode)
            return;

        await ExecuteSessionTransitionAsync(_resultSessions.SwitchMode(mode), "模式切换");
    }

    private async void OnRefreshRequested()
    {
        if (_floatingWindow is null)
            return;

        if (_resultSessions.ActiveOperation is
            {
                Kind: FloatingResultActiveOperationKind.Root,
                RootIdentity: { } activeIdentity
            } &&
            _resultSessions.CurrentSession is { } activeSession &&
            activeSession.SessionId == activeIdentity.SessionId &&
            activeSession.ActiveMode == activeIdentity.Mode)
        {
            CancelActiveTranslationRequest();
            _floatingWindow.ShowSelectionCaptureFeedback("已停止生成");
            return;
        }
        await ExecuteSessionTransitionAsync(
            _resultSessions.RefreshMode(),
            "重新生成",
            TranslationCacheReadMode.BypassCache);
    }

    private async void OnModelProfileSelected(string profileId)
    {
        if (_settings is null || _floatingWindow is null ||
            _resultSessions.CurrentSession is not { } session)
        {
            return;
        }

        // A running follow-up owns its presentation identity. Do not replace it
        // from the model menu; the new model takes effect when the user retries.
        if (session.ActiveMode == ContentType.Analysis &&
            _resultSessions.ActiveOperation is { Kind: FloatingResultActiveOperationKind.FollowUp })
        {
            return;
        }

        var profile = BuildAvailableModelProfiles()
            .FirstOrDefault(candidate => string.Equals(candidate.Id, profileId, StringComparison.Ordinal));
        var mode = session.ActiveMode;
        var state = session.ModeStates[mode];
        var previousProfile = _modelSelection.GetCurrentProfile(mode);
        var decision = _modelSelection.Select(
            session.SessionId,
            mode,
            profile,
            state.Status == ModeResultStatus.Loading);
        if (decision.Intent == ModelSelectionIntent.NoOp)
            return;
        if (decision.Intent == ModelSelectionIntent.OpenSettings || decision.Request is null)
        {
            OnSettingsRequested();
            _settingsWindow?.ShowConfigurationNotice(
                "请补全并保存模型配置后再切换。",
                isWarning: false);
            return;
        }

        _translationMetrics.RecordModelSwitchRequested();
        Logger.Info("App", "translation.model_switch_requested", new
        {
            content_type = mode.ToString(),
            from_model = previousProfile?.ModelName ?? "unknown",
            from_provider = previousProfile?.ProviderName ?? "unknown",
            to_model = decision.Profile?.ModelName ?? "unknown",
            to_provider = decision.Profile?.ProviderName ?? "unknown",
            request_running = state.Status == ModeResultStatus.Loading
        });
        RefreshFloatingModelSelector();
        if (mode == ContentType.Analysis &&
            session.AnalysisConversation.Turns.Count > 0)
        {
            _floatingWindow.ShowSelectionCaptureFeedback("已切换后续追问模型，重试或下一轮追问时生效");
            return;
        }
        await ExecuteSessionTransitionAsync(
            _resultSessions.RefreshMode(),
            "切换模型",
            TranslationCacheReadMode.BypassCache,
            decision.Request);
    }

    private async void OnTranslationDirectionToggleRequested()
    {
        if (_floatingWindow is null ||
            _resultSessions.CurrentSession is not { ActiveMode: ContentType.Translation } session)
        {
            return;
        }

        var currentDirection = ResolveTranslationDirection(session);
        var requestWasRunning = session.ModeStates[ContentType.Translation].Status == ModeResultStatus.Loading;
        var preference = string.Equals(
            currentDirection.EffectiveTargetLanguage,
            session.RequestContext.FallbackLanguage,
            StringComparison.Ordinal)
            ? TranslationDirectionPreference.RequestedTarget
            : TranslationDirectionPreference.FallbackTarget;
        var transition = _resultSessions.SwitchTranslationDirection(preference);
        if (transition.Kind != FloatingResultSessionTransitionKind.StartedRequest)
            return;

        var nextDirection = ResolveTranslationDirection(session);
        Logger.Info("App", "translation.direction_switched", new
        {
            from_target = currentDirection.EffectiveTargetLanguage,
            to_target = nextDirection.EffectiveTargetLanguage,
            request_running = requestWasRunning,
            model = _modelSelection.CurrentProfile?.ModelName ?? "unknown",
            provider = _modelSelection.CurrentProfile?.ProviderName ?? "unknown"
        });
        await ExecuteSessionTransitionAsync(transition, "切换翻译方向");
    }

    private static void LogEchoDetection(
        string eventName,
        TranslationRequest request,
        string result,
        TranslationEchoDetectionResult detection,
        bool confirmed)
    {
        var context = new
        {
            model = request.ModelName,
            provider = GetProviderHost(request.ApiBaseUrl),
            source_len = request.Text.Length,
            result_len = result.Length,
            similarity = detection.Similarity,
            length_ratio = detection.LengthRatio,
            reason = detection.Reason
        };
        if (confirmed)
            Logger.Warn("App", eventName, context);
        else
            Logger.Info("App", eventName, context);
    }

    private static string GetProviderHost(string apiBaseUrl) =>
        Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri) ? uri.Host : "unknown";

    private void OnHideRequested()
    {
        if (_resultSessions.CancelActiveFollowUp())
            _translationRequests.Cancel();
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

    private void OnAnalysisDraftChanged(Guid sessionId, string draft) =>
        _resultSessions.TrySetAnalysisDraft(sessionId, draft);

    private async void OnAnalysisFollowUpRequested(string question)
    {
        try
        {
            await ExecuteAnalysisFollowUpAsync(_resultSessions.BeginFollowUp(question));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _floatingWindow?.ShowAnalysisFollowUpFeedback(ex.Message);
        }
    }

    private async void OnAnalysisFollowUpRetryRequested()
    {
        try
        {
            await ExecuteAnalysisFollowUpAsync(_resultSessions.RetryLatestFollowUp());
        }
        catch (InvalidOperationException ex)
        {
            _floatingWindow?.ShowAnalysisFollowUpFeedback(ex.Message);
        }
    }

    private Task StartSessionRequestAsync(
        string text,
        ContentType contentType,
        FloatingWindowAnchor floatingAnchor,
        string operationName,
        DetectionResult? detection = null)
    {
        _modelSelection.Reset();
        var requestContext = _translationService!.CaptureRequestContext(_settings!.TargetLanguage);
        var transition = _resultSessions.StartSession(
            text,
            floatingAnchor,
            contentType,
            detection,
            requestContext);
        return ExecuteSessionTransitionAsync(transition, operationName);
    }

    private async Task ExecuteSessionTransitionAsync(
        FloatingResultSessionTransition transition,
        string operationName,
        TranslationCacheReadMode cacheReadMode = TranslationCacheReadMode.UseCache,
        TranslationRequest? requestOverride = null)
    {
        if (_floatingWindow is null || transition.Session is null)
            return;

        if (transition.Kind == FloatingResultSessionTransitionKind.RestoredCompleted)
        {
            _translationRequests.Cancel();
            var state = transition.Session.ModeStates[transition.Session.ActiveMode];
            var displayRequest = CreateDisplayRequest(
                transition.Session,
                transition.Session.ActiveMode);
            EnsureModelSelection(transition.Session, displayRequest);
            RefreshFloatingModelSelector();
            var presentationId = _floatingWindow.BeginReplacement(
                _resultSessions.CurrentPresentationId,
                string.Equals(operationName, "模式切换", StringComparison.Ordinal)
                    ? FloatingWindow.ThoughtResetScope.PreserveSession
                    : FloatingWindow.ThoughtResetScope.ClearActiveMode);
            await ShowRequestResultAsync(
                displayRequest,
                state.RawText,
                transition.Session.Anchor ?? _floatingWindow.CurrentAnchor,
                presentationId);
            _floatingWindow.SetSessionView(
                transition.Session.SessionId,
                transition.Session.ActiveMode,
                state,
                transition.Session.AnalysisConversation);
            RefreshFloatingTranslationDirectionState();
            return;
        }

        if (transition.Kind != FloatingResultSessionTransitionKind.StartedRequest ||
            transition.RequestIdentity is not { } identity ||
            transition.Session.Anchor is not { } anchor)
        {
            return;
        }

        var visualPresentationId = _floatingWindow.BeginReplacement(
            identity.PresentationId,
            string.Equals(operationName, "模式切换", StringComparison.Ordinal)
                ? FloatingWindow.ThoughtResetScope.PreserveSession
                : FloatingWindow.ThoughtResetScope.ClearActiveMode);
        var request = requestOverride ?? CreateDisplayRequest(transition.Session, identity.Mode);
        EnsureModelSelection(transition.Session, request);
        RefreshFloatingModelSelector();
        _floatingWindow.SetSessionView(
            transition.Session.SessionId,
            transition.Session.ActiveMode,
            transition.Session.ModeStates[transition.Session.ActiveMode],
            transition.Session.AnalysisConversation);
        RefreshFloatingTranslationDirectionState();
        await ExecuteRequestAsync(
            identity.Mode,
            anchor,
            visualPresentationId,
            identity,
            operationName,
            cacheReadMode,
            request);
    }

    private TranslationRequest CreateDisplayRequest(FloatingResultSession session, ContentType contentType)
    {
        var semanticRequest = _translationService!.CreateRequest(
            session.SourceText,
            contentType,
            contentType == ContentType.Analysis
                ? TranslationRequestKind.Analysis
                : TranslationRequestKind.Translation,
            session.RequestContext,
            session.TranslationDirectionPreference);
        if (_modelSelection.TryApplyCurrentProfile(
                session.SessionId,
                contentType,
                semanticRequest,
                out var profiledRequest) &&
            profiledRequest is not null)
        {
            return profiledRequest;
        }

        return semanticRequest;
    }

    private void EnsureModelSelection(FloatingResultSession session, TranslationRequest request)
    {
        if (_modelSelection.IsCurrent(session.SessionId, session.ActiveMode))
        {
            return;
        }

        var savedProfile = (_settings?.SavedConfigs ?? [])
            .Select(ModelProfileCatalog.Create)
            .FirstOrDefault(profile => ModelProfileCatalog.Matches(profile, request));
        _modelSelection.BeginSession(
            session.SessionId,
            session.ActiveMode,
            savedProfile ?? ModelProfileCatalog.CreateCurrent(request),
            request);
    }

    private IReadOnlyList<ModelProfile> BuildAvailableModelProfiles()
    {
        var profiles = (_settings?.SavedConfigs ?? [])
            .Select(ModelProfileCatalog.Create)
            .ToList();
        if (_modelSelection.CurrentProfile is { } current &&
            !profiles.Any(profile => string.Equals(profile.Id, current.Id, StringComparison.Ordinal)))
        {
            profiles.Insert(0, current);
        }
        return profiles;
    }

    private void RefreshFloatingModelSelector()
    {
        if (_floatingWindow is null || _resultSessions.CurrentSession is not { } session)
            return;

        var enabled = session.ActiveMode != ContentType.Analysis ||
            _resultSessions.ActiveOperation is not { Kind: FloatingResultActiveOperationKind.FollowUp };
        _floatingWindow.SetModelProfiles(
            BuildAvailableModelProfiles(),
            _modelSelection.GetCurrentProfile(session.ActiveMode),
            enabled);
    }

    private void RefreshFloatingTranslationDirectionState()
    {
        if (_floatingWindow is null || _resultSessions.CurrentSession is not { } session)
            return;

        if (session.ActiveMode != ContentType.Translation ||
            string.IsNullOrWhiteSpace(session.RequestContext.RequestedTargetLanguage) ||
            string.IsNullOrWhiteSpace(session.RequestContext.FallbackLanguage) ||
            string.Equals(
                session.RequestContext.RequestedTargetLanguage,
                session.RequestContext.FallbackLanguage,
                StringComparison.Ordinal))
        {
            _floatingWindow.SetTranslationDirectionState(null, null, false, enabled: false);
            return;
        }

        var direction = ResolveTranslationDirection(session);
        var alternateTargetLanguage = string.Equals(
            direction.EffectiveTargetLanguage,
            session.RequestContext.FallbackLanguage,
            StringComparison.Ordinal)
            ? session.RequestContext.RequestedTargetLanguage
            : session.RequestContext.FallbackLanguage;
        _floatingWindow.SetTranslationDirectionState(
            direction.EffectiveTargetLanguage,
            alternateTargetLanguage,
            session.TranslationDirectionPreference != TranslationDirectionPreference.Auto,
            enabled: true);
    }

    private static TranslationDirectionDecision ResolveTranslationDirection(FloatingResultSession session) =>
        TranslationDirectionResolver.Resolve(
            session.SourceText,
            session.RequestContext.RequestedTargetLanguage,
            session.RequestContext.FallbackLanguage,
            session.RequestContext.AutoDetectLanguage,
            ContentType.Translation,
            session.TranslationDirectionPreference);

    private async Task ExecuteRequestAsync(
        ContentType contentType,
        FloatingWindowAnchor floatingAnchor,
        long presentationId,
        FloatingResultRequestIdentity sessionIdentity,
        string operationName,
        TranslationCacheReadMode cacheReadMode,
        TranslationRequest request)
    {
        if (_translationService == null || _settings == null || _floatingWindow == null)
            return;

        var requestScope = BeginTranslationRequest();
        var startedAt = Stopwatch.GetTimestamp();
        var isModelSwitch = string.Equals(operationName, "切换模型", StringComparison.Ordinal);

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
                if (!_resultSessions.TryComplete(
                    sessionIdentity,
                    cachedResult,
                    CreateAnalysisSemanticSnapshot(request)))
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
                    request.EffectiveTargetLanguage,
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

            var presentedText = new StringBuilder();
            var reasoningPresentedText = new StringBuilder();
            var reasoningAccumulator = new ReasoningSummaryAccumulator();
            var dispatcherMetrics = new StreamingDispatcherMetrics();
            var runtimeStart = StreamingRuntimeStats.Capture();
            await using var presentationPump = new StreamingPresentationPump(
                (frame, cancellationToken) =>
                {
                    var queuedAt = Stopwatch.GetTimestamp();
                    return Dispatcher.InvokeAsync(() =>
                    {
                        var executionStarted = Stopwatch.GetTimestamp();
                        var queueDelay = Stopwatch.GetElapsedTime(queuedAt, executionStarted);
                        try
                        {
                            presentedText.Append(frame.Delta);
                            var snapshot = presentedText.ToString();
                            if (IsCurrentRequest(requestScope) &&
                                _resultSessions.TryUpdateStreaming(sessionIdentity, snapshot) &&
                                _floatingWindow?.IsPresentationCurrent(presentationId) == true)
                            {
                                _floatingWindow.UpdateTranslation(presentationId, snapshot);
                            }
                        }
                        finally
                        {
                            dispatcherMetrics.Record(
                                queueDelay,
                                Stopwatch.GetElapsedTime(executionStarted));
                        }
                    }, StreamingDispatcherMetrics.PresentationPriority, cancellationToken).Task;
                });
            await using var reasoningPump = new StreamingPresentationPump(
                (frame, cancellationToken) =>
                    Dispatcher.InvokeAsync(() =>
                    {
                        reasoningPresentedText.Append(frame.Delta);
                        if (IsCurrentRequest(requestScope) &&
                            _floatingWindow?.IsPresentationCurrent(presentationId) == true)
                        {
                            _floatingWindow.UpdateRootThought(
                                presentationId,
                                reasoningPresentedText.ToString(),
                                reasoningAccumulator.IsTruncated);
                        }
                    }, StreamingDispatcherMetrics.PresentationPriority, cancellationToken).Task);
            var result = await _translationService.ExecuteStreamingEventsAsync(
                request,
                streamEvent =>
                {
                    if (streamEvent.Kind == TranslationStreamEventKind.ContentDelta)
                    {
                        presentationPump.Publish(streamEvent.Text ?? string.Empty);
                    }
                    else if (streamEvent.Kind == TranslationStreamEventKind.ReasoningDelta)
                    {
                        var accepted = reasoningAccumulator.Append(streamEvent.Text);
                        reasoningPump.Publish(accepted);
                    }
                },
                requestScope.Token);
            var presentationStats = await presentationPump.CompleteAsync();
            await reasoningPump.CompleteAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                if (IsCurrentRequest(requestScope) &&
                    _floatingWindow?.IsPresentationCurrent(presentationId) == true)
                {
                    _floatingWindow.CompleteRootThought(
                        presentationId,
                        reasoningAccumulator.IsTruncated);
                }
            }, StreamingDispatcherMetrics.PresentationPriority, requestScope.Token);
            var dispatcherStats = dispatcherMetrics.GetStats();
            var markdownStats = _floatingWindow.GetStreamingMarkdownStats();
            var compositionStats = _floatingWindow.GetStreamingCompositionStats();
            var runtimeStats = StreamingRuntimeStats.Capture().Since(runtimeStart);

            requestScope.Token.ThrowIfCancellationRequested();
            if (!IsCurrentRequest(requestScope))
            {
                _translationMetrics.RecordExpired();
                return;
            }

            // Echo quality checks run only after the streamed result has been presented.
            var echoDetection = contentType == ContentType.Translation
                ? TranslationEchoDetector.Detect(request.Text, result)
                : null;
            if (echoDetection?.Confidence == TranslationEchoConfidence.Suspected)
            {
                _translationMetrics.RecordEchoSuspected();
                LogEchoDetection(
                    "translation.echo_suspected",
                    request,
                    result,
                    echoDetection,
                    confirmed: false);
            }

            if (echoDetection?.IsConfirmed == true)
            {
                _translationMetrics.RecordEchoConfirmed();
                if (isModelSwitch)
                    _translationMetrics.RecordModelSwitchFailed();
                LogEchoDetection(
                    "translation.echo_confirmed",
                    request,
                    result,
                    echoDetection,
                    confirmed: true);
                if (IsCurrentRequest(requestScope) &&
                    _resultSessions.TryCompleteWithEchoWarning(sessionIdentity, result) &&
                    _floatingWindow.IsPresentationCurrent(presentationId))
                {
                    _floatingWindow.FlushStreamingUpdate();
                    UpdateFloatingSessionView();
                }
                return;
            }

            if (!_resultSessions.TryComplete(
                sessionIdentity,
                result,
                CreateAnalysisSemanticSnapshot(request)))
            {
                _translationMetrics.RecordExpired();
                return;
            }

            _floatingWindow?.FlushStreamingUpdate();
            UpdateFloatingSessionView();
            _translationCache.Set(request, result);
            SaveTranslationHistory(
                request.Text,
                result,
                request.EffectiveTargetLanguage,
                request.ContentType,
                request.ModelName);
            var duration = Stopwatch.GetElapsedTime(startedAt);
            _translationMetrics.RecordCompleted(duration);
            if (isModelSwitch)
                _translationMetrics.RecordModelSwitchCompleted();
            Logger.Info("App", "translation.presented", new
            {
                operation = operationName,
                content_type = request.ContentType.ToString(),
                model = request.ModelName,
                provider = GetProviderHost(request.ApiBaseUrl),
                result_len = result.Length,
                duration_ms = duration.TotalMilliseconds,
                stream_chunk_count = presentationStats.PublishedChunkCount,
                ui_frame_count = presentationStats.AppliedFrameCount,
                coalesced_chunk_count = presentationStats.CoalescedChunkCount,
                first_frame_latency_ms = presentationStats.FirstFrameLatencyMs,
                max_frame_latency_ms = presentationStats.MaxFrameLatencyMs,
                average_ui_apply_ms = presentationStats.AverageApplyDurationMs,
                max_ui_apply_ms = presentationStats.MaxApplyDurationMs,
                final_frame_interval_ms = presentationStats.FinalFrameIntervalMs,
                average_dispatcher_queue_ms = dispatcherStats.AverageQueueDelayMs,
                max_dispatcher_queue_ms = dispatcherStats.MaxQueueDelayMs,
                average_ui_execution_ms = dispatcherStats.AverageExecutionDurationMs,
                max_ui_execution_ms = dispatcherStats.MaxExecutionDurationMs,
                markdown_frame_count = markdownStats.FrameCount,
                average_markdown_render_ms = markdownStats.AverageRenderDurationMs,
                max_markdown_render_ms = markdownStats.MaxRenderDurationMs,
                markdown_allocated_bytes = markdownStats.AllocatedBytes,
                markdown_parsed_characters = markdownStats.ParsedCharacters,
                gc_gen0_collections = runtimeStats.Gen0Collections,
                gc_gen1_collections = runtimeStats.Gen1Collections,
                gc_gen2_collections = runtimeStats.Gen2Collections,
                gc_pause_ms = runtimeStats.GcPauseDurationMs,
                runtime_allocated_bytes = runtimeStats.AllocatedBytes,
                composition_requested_frame_count = compositionStats.RequestedFrameCount,
                composition_presented_frame_count = compositionStats.PresentedFrameCount,
                composition_coalesced_request_count = compositionStats.CoalescedRequestCount,
                average_composition_wait_ms = compositionStats.AverageWaitDurationMs,
                max_composition_wait_ms = compositionStats.MaxWaitDurationMs
            });
        }
        catch (OperationCanceledException) when (requestScope.Token.IsCancellationRequested || !IsCurrentRequest(requestScope))
        {
            _floatingWindow.CancelRootThought(presentationId, false);
            _resultSessions.TryCancel(sessionIdentity);
            _translationMetrics.RecordCancelled();
            Logger.Debug("App", "translation.cancelled", new { operation = operationName, request_id = requestScope.RequestId });
        }
        catch (Exception ex)
        {
            _floatingWindow.FailRootThought(presentationId, false);
            if (isModelSwitch)
                _translationMetrics.RecordModelSwitchFailed();
            if (IsCurrentRequest(requestScope))
                _translationMetrics.RecordFailed();
            else
                _translationMetrics.RecordExpired();
            Logger.Error("App", "translation.failed", new
            {
                operation = operationName,
                request_id = requestScope.RequestId,
                model = request.ModelName,
                provider = GetProviderHost(request.ApiBaseUrl),
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

    private async Task ExecuteAnalysisFollowUpAsync(AnalysisFollowUpTransition transition)
    {
        if (_translationService == null || _floatingWindow == null)
            return;

        UpdateFloatingSessionView();
        var identity = transition.RequestIdentity;
        var presentationId = identity.PresentationId;
        var requestScope = BeginTranslationRequest();

        try
        {
            requestScope.Token.ThrowIfCancellationRequested();
            _floatingWindow.BeginFollowUpThought(transition.Turn.TurnNumber);
            var conversation = transition.Session.AnalysisConversation;
            var semanticSnapshot = conversation.SemanticSnapshot
                ?? throw new InvalidOperationException("当前解析结果不能追问");
            var rootAnalysis = transition.Session.ModeStates[ContentType.Analysis].RawText;
            var request = _translationService.CreateAnalysisFollowUpRequest(
                transition.Session.SourceText,
                rootAnalysis,
                semanticSnapshot,
                _resultSessions.GetCompletedFollowUpExchanges(transition.Session.SessionId),
                transition.Turn.Question,
                transition.Turn.TurnNumber,
                identity.RequestId,
                BuildFollowUpRequestContext(transition.Session));

            var presentedText = new StringBuilder();
            var reasoningPresentedText = new StringBuilder();
            var reasoningAccumulator = new ReasoningSummaryAccumulator();
            var dispatcherMetrics = new StreamingDispatcherMetrics();
            var runtimeStart = StreamingRuntimeStats.Capture();
            await using var presentationPump = new StreamingPresentationPump(
                (frame, cancellationToken) =>
                {
                    var queuedAt = Stopwatch.GetTimestamp();
                    return Dispatcher.InvokeAsync(() =>
                    {
                        var executionStarted = Stopwatch.GetTimestamp();
                        var queueDelay = Stopwatch.GetElapsedTime(queuedAt, executionStarted);
                        try
                        {
                            presentedText.Append(frame.Delta);
                            var snapshot = presentedText.ToString();
                            if (!IsCurrentRequest(requestScope) ||
                                !_resultSessions.TryUpdateFollowUpStreaming(identity, snapshot) ||
                                _floatingWindow?.IsPresentationCurrent(presentationId) != true)
                            {
                                return;
                            }

                            var currentTurn = _resultSessions.CurrentSession?
                                .AnalysisConversation.Turns.LastOrDefault();
                            if (currentTurn is not null && currentTurn.LastRequestId == identity.RequestId)
                                _floatingWindow.UpdateAnalysisFollowUpStreaming(presentationId, currentTurn);
                        }
                        finally
                        {
                            dispatcherMetrics.Record(
                                queueDelay,
                                Stopwatch.GetElapsedTime(executionStarted));
                        }
                    }, StreamingDispatcherMetrics.PresentationPriority, cancellationToken).Task;
                });
            await using var reasoningPump = new StreamingPresentationPump(
                (frame, cancellationToken) =>
                    Dispatcher.InvokeAsync(() =>
                    {
                        reasoningPresentedText.Append(frame.Delta);
                        if (IsCurrentRequest(requestScope) &&
                            _floatingWindow?.IsPresentationCurrent(presentationId) == true)
                        {
                            _floatingWindow.UpdateFollowUpThought(
                                presentationId,
                                identity.TurnNumber,
                                reasoningPresentedText.ToString(),
                                reasoningAccumulator.IsTruncated);
                        }
                    }, StreamingDispatcherMetrics.PresentationPriority, cancellationToken).Task);
            var result = await _translationService.ExecuteAnalysisFollowUpStreamingEventsAsync(
                request,
                streamEvent =>
                {
                    if (streamEvent.Kind == TranslationStreamEventKind.ContentDelta)
                    {
                        presentationPump.Publish(streamEvent.Text ?? string.Empty);
                    }
                    else if (streamEvent.Kind == TranslationStreamEventKind.ReasoningDelta)
                    {
                        var accepted = reasoningAccumulator.Append(streamEvent.Text);
                        reasoningPump.Publish(accepted);
                    }
                },
                requestScope.Token);
            var presentationStats = await presentationPump.CompleteAsync();
            await reasoningPump.CompleteAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                if (IsCurrentRequest(requestScope) &&
                    _floatingWindow?.IsPresentationCurrent(presentationId) == true)
                {
                    _floatingWindow.CompleteFollowUpThought(
                        presentationId,
                        identity.TurnNumber,
                        reasoningAccumulator.IsTruncated);
                }
            }, StreamingDispatcherMetrics.PresentationPriority, requestScope.Token);
            var dispatcherStats = dispatcherMetrics.GetStats();
            var markdownStats = _floatingWindow.GetAnalysisFollowUpStreamingStats(identity.TurnNumber);
            var compositionStats = _floatingWindow.GetAnalysisFollowUpCompositionStats(identity.TurnNumber);
            var runtimeStats = StreamingRuntimeStats.Capture().Since(runtimeStart);

            requestScope.Token.ThrowIfCancellationRequested();
            if (!IsCurrentRequest(requestScope) ||
                !_resultSessions.TryCompleteFollowUp(identity, result) ||
                !_floatingWindow.IsPresentationCurrent(presentationId))
            {
                return;
            }

            UpdateFloatingSessionView();
            Logger.Info("App", "analysis.follow_up.presented", new
            {
                turn = identity.TurnNumber,
                request_id = identity.RequestId,
                stream_chunk_count = presentationStats.PublishedChunkCount,
                ui_frame_count = presentationStats.AppliedFrameCount,
                coalesced_chunk_count = presentationStats.CoalescedChunkCount,
                first_frame_latency_ms = presentationStats.FirstFrameLatencyMs,
                max_frame_latency_ms = presentationStats.MaxFrameLatencyMs,
                average_ui_apply_ms = presentationStats.AverageApplyDurationMs,
                max_ui_apply_ms = presentationStats.MaxApplyDurationMs,
                final_frame_interval_ms = presentationStats.FinalFrameIntervalMs,
                average_dispatcher_queue_ms = dispatcherStats.AverageQueueDelayMs,
                max_dispatcher_queue_ms = dispatcherStats.MaxQueueDelayMs,
                average_ui_execution_ms = dispatcherStats.AverageExecutionDurationMs,
                max_ui_execution_ms = dispatcherStats.MaxExecutionDurationMs,
                markdown_frame_count = markdownStats.FrameCount,
                average_markdown_render_ms = markdownStats.AverageRenderDurationMs,
                max_markdown_render_ms = markdownStats.MaxRenderDurationMs,
                markdown_allocated_bytes = markdownStats.AllocatedBytes,
                markdown_parsed_characters = markdownStats.ParsedCharacters,
                gc_gen0_collections = runtimeStats.Gen0Collections,
                gc_gen1_collections = runtimeStats.Gen1Collections,
                gc_gen2_collections = runtimeStats.Gen2Collections,
                gc_pause_ms = runtimeStats.GcPauseDurationMs,
                runtime_allocated_bytes = runtimeStats.AllocatedBytes,
                composition_requested_frame_count = compositionStats.RequestedFrameCount,
                composition_presented_frame_count = compositionStats.PresentedFrameCount,
                composition_coalesced_request_count = compositionStats.CoalescedRequestCount,
                average_composition_wait_ms = compositionStats.AverageWaitDurationMs,
                max_composition_wait_ms = compositionStats.MaxWaitDurationMs
            });
        }
        catch (OperationCanceledException) when (requestScope.Token.IsCancellationRequested || !IsCurrentRequest(requestScope))
        {
            _floatingWindow.CancelFollowUpThought(presentationId, identity.TurnNumber, false);
            if (_resultSessions.TryCancelFollowUp(identity) &&
                _floatingWindow.IsPresentationCurrent(presentationId))
            {
                UpdateFloatingSessionView();
            }
        }
        catch (Exception ex)
        {
            _floatingWindow.FailFollowUpThought(presentationId, identity.TurnNumber, false);
            if (IsCurrentRequest(requestScope) &&
                _resultSessions.TryFailFollowUp(identity) &&
                _floatingWindow.IsPresentationCurrent(presentationId))
            {
                UpdateFloatingSessionView();
                _floatingWindow.ShowAnalysisFollowUpFeedback(
                    ex is InvalidOperationException { Message: "当前解析内容过长，无法继续追问" }
                        ? "当前解析内容过长，无法继续追问"
                        : "追问失败，请重试本轮。");
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
            session.ModeStates[session.ActiveMode],
            session.AnalysisConversation);
        RefreshFloatingTranslationDirectionState();
        RefreshFloatingModelSelector();
        _trayIcon?.SetRestoreAvailable(
            session.ModeStates.Values.Any(state =>
                state.Status != ModeResultStatus.NotStarted ||
                !string.IsNullOrWhiteSpace(state.RawText)));
    }

    private static AnalysisSemanticSnapshot? CreateAnalysisSemanticSnapshot(TranslationRequest request) =>
        request.Kind == TranslationRequestKind.Analysis
            ? new AnalysisSemanticSnapshot(
                request.SystemPrompt,
                request.EffectiveTargetLanguage,
                request.ModelName)
            : null;

    private TranslationRequestContext BuildFollowUpRequestContext(FloatingResultSession session)
    {
        var context = session.RequestContext;
        if (_modelSelection.GetCurrentProfile(ContentType.Analysis) is not { IsComplete: true } profile)
            return context;

        return context with
        {
            ApiBaseUrl = profile.ApiBaseUrl,
            ApiKey = profile.ApiKey,
            ModelName = profile.ModelName
        };
    }

    private async void OnAnalysisFollowUpReplaceRequested(int turnNumber, string question)
    {
        try
        {
            await ExecuteAnalysisFollowUpAsync(_resultSessions.ReplaceFollowUp(turnNumber, question));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _floatingWindow?.ShowAnalysisFollowUpFeedback(ex.Message);
        }
    }

    private void OnAnalysisFollowUpStopRequested()
    {
        if (!_resultSessions.StopActiveFollowUpForEditing())
            return;

        _translationRequests.Cancel();
        UpdateFloatingSessionView();
    }

    private async Task ShowMessageWithoutReplacingSessionAsync(
        string message,
        FloatingWindowAnchor anchor)
    {
        if (_floatingWindow is null)
            return;

        if (_resultSessions.CurrentSession is not null)
        {
            _floatingWindow.ShowExistingResult();
            _floatingWindow.ShowSelectionCaptureFeedback(message);
            return;
        }

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
        UpdateFloatingSessionView();
    }

    private async Task<bool> ShowRequestLoadingAsync(
        TranslationRequest request,
        FloatingWindowAnchor floatingAnchor,
        long presentationId)
    {
        _floatingWindow!.SetLoading(true);
        var loadingText = request.Kind == TranslationRequestKind.Analysis
            ? "解析中..."
            : "翻译中...";
        var shown = await _floatingWindow!.ShowTranslationAsync(
            presentationId,
            loadingText,
            floatingAnchor,
            request.ContentType,
            request.FallbackUsed ? request.Text : null);
        if (shown)
            _floatingWindow.BeginRootThought(presentationId);
        return shown;
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

    internal static bool IsSelectionGestureConsistent(
        SelectionLocation location,
        SelectionIntent intent)
    {
        if (!location.IsValid || location.Bounds.IsEmpty)
            return false;

        // UIA and low-level mouse hooks both use physical screen pixels here.
        // A small expansion tolerates glyph edges without accepting a window
        // drag whose stale focused selection is elsewhere on the screen.
        var bounds = location.Bounds;
        bounds.Inflate(24, 24);
        return intent.GestureKind switch
        {
            SelectionGestureKind.MultiClick => bounds.Contains(intent.StartPoint),
            SelectionGestureKind.Drag =>
                bounds.Contains(intent.StartPoint) || bounds.Contains(intent.EndPoint),
            _ => true
        };
    }

    /// <summary>
    /// 红点取消处理（点击其他位置或自动隐藏）
    /// </summary>
    private void OnSelectionCancelled()
    {
        Interlocked.Increment(ref _selectionGeneration);
        _selectionCts?.Cancel();
        _selectionCts = null;
        if (_selectionDetector is not null)
            _selectionDetector.IsRedDotVisible = false;
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

            _settingsWindow = new SettingsWindow(
                _settings!,
                OnSettingsSaved,
                OnFeedbackRequested,
                OnLogsRequested);
            _settingsWindow.Closed += OnSettingsWindowClosed;
            _settingsWindow.Show();
        });
    }

    private void OnModelSettingsRequested()
    {
        if (_floatingWindow is not null && _resultSessions.CurrentSession is { } session)
        {
            if (_modelSettingsContext is null)
                _floatingWindow.SuspendAutoHide();
            _modelSettingsContext = new ModelSettingsContext(session.SessionId, session.ActiveMode);
        }
        OnSettingsRequested();
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        _settingsWindow = null;
        if (_crashRecoveryPromptWindow is null &&
            _pendingRecoveryEvent is { PromptState: RecoveryPromptState.Pending } &&
            _settings?.CrashFeedbackPromptEnabled == true)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowRecoveryPromptIfPending));
        }
        if (_modelSettingsContext is not { } context)
            return;

        _modelSettingsContext = null;
        _floatingWindow?.ResumeAutoHide();
        if (_isExiting ||
            _floatingWindow is null ||
            _resultSessions.CurrentSession is not { } session ||
            session.SessionId != context.SessionId ||
            session.ActiveMode != context.Mode)
        {
            return;
        }

        UpdateFloatingSessionView();
        if (_floatingWindow.ShowExistingResult())
            _floatingWindow.Activate();
    }

    private void ShowStartupConfiguration(ConfigLoadStatus status)
    {
        OnSettingsRequested();
        if (_settingsWindow is null)
            return;

        if (status == ConfigLoadStatus.FirstLaunch)
        {
            _settingsWindow.ShowConfigurationNotice(
                "首次使用，请确认供应商和模型并填写 API Key。",
                isWarning: false);
        }
        else if (status == ConfigLoadStatus.Corrupted)
        {
            _settingsWindow.ShowConfigurationNotice(
                "原配置无法读取，已加载安全默认值；原文件仍保留，请确认后重新保存。",
                isWarning: true);
        }
    }

    /// <summary>
    /// 设置保存回调 - 更新翻译服务和状态
    /// </summary>
    private void OnSettingsSaved(AppSettings settings)
    {
        CancelActiveTranslationRequest();
        _translationCache.Clear();
        _settings = settings;
        var refreshedCurrentProfile = settings.SavedConfigs
            .Select(ModelProfileCatalog.Create)
            .FirstOrDefault(profile =>
                _modelSelection.CurrentProfile is { } current &&
                (string.Equals(profile.Id, current.Id, StringComparison.Ordinal) ||
                 (string.Equals(profile.ApiBaseUrl.TrimEnd('/'), current.ApiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(profile.ApiKey, current.ApiKey, StringComparison.Ordinal) &&
                  string.Equals(profile.ModelName, current.ModelName, StringComparison.Ordinal))));
        if (refreshedCurrentProfile is not null)
            _modelSelection.RefreshCurrentProfile(refreshedCurrentProfile);
        _translationService?.UpdateSettings(settings);
        _openAiWordLookupService?.UpdateSettings(CreateWordLookupSettings(settings));
        Logger.Configure(
            Logger.ParseLevel(settings.LogLevel),
            settings.LogRetentionDays,
            settings.LogMaxTotalBytes);

        if (!settings.TtsEnabled)
        {
            _ = _ttsPlayback?.StopAsync(TtsPlaybackOwner.FloatingResult);
            _ = _ttsPlayback?.StopAsync(TtsPlaybackOwner.QuickLookup);
        }
        _floatingWindow?.ApplyTtsSettings(
            settings.TtsEnabled,
            settings.TtsVoice,
            settings.TtsRate,
            settings.TtsMaxChars);
        _quickLookupWindow?.ApplySettings(
            settings.TargetLanguage,
            settings.TtsEnabled,
            settings.TtsVoice,
            settings.TtsRate,
            settings.TtsMaxChars);

        OnSelectionCancelled();
        ApplyHotKeyConfiguration(settings);
        ApplyQuickLookupHotKeyConfiguration(settings);
        _trayIcon?.SetPaused(TranslationTriggerModes.IsPaused(settings.TranslationTriggerMode));

        RefreshFloatingModelSelector();
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

    private void OnFeedbackRequested(FeedbackMode mode)
    {
        Dispatcher.BeginInvoke(new Action(() => OpenFeedbackWindow(mode)));
    }

    private void OpenFeedbackWindow(FeedbackMode mode, FeedbackDiagnosticSummary? diagnostics = null)
    {
        if (_feedbackWindow is not null)
        {
            _feedbackWindow.Activate();
            return;
        }

        _feedbackWindow = new FeedbackWindow(mode, diagnostics);
        _feedbackWindow.Closed += (_, _) => _feedbackWindow = null;
        _feedbackWindow.Show();
    }

    private void ShowRecoveryPromptIfPending()
    {
        if (_crashRecoveryPromptWindow is not null ||
            _settingsWindow is not null ||
            _settings is null ||
            !_settings.CrashFeedbackPromptEnabled ||
            _crashRecoveryTracker?.PendingEvent is not { } recovery)
            return;

        // Mark before showing so a prompt creation failure cannot repeat forever.
        _crashRecoveryTracker.MarkShown();
        try
        {
            var prompt = new CrashRecoveryPromptWindow();
            var handled = false;
            _crashRecoveryPromptWindow = prompt;
            prompt.Closed += (_, _) =>
            {
                if (!handled)
                    _crashRecoveryTracker.MarkDismissed();
                if (ReferenceEquals(_crashRecoveryPromptWindow, prompt))
                    _crashRecoveryPromptWindow = null;
            };
            prompt.FeedbackRequested += (_, _) =>
            {
                if (handled)
                    return;
                handled = true;
                _crashRecoveryTracker.MarkFeedbackStarted();
                prompt.Close();
                OpenFeedbackWindow(
                    FeedbackMode.CrashRecovery,
                    new FeedbackDiagnosticSummary(
                        typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
                        Environment.OSVersion.VersionString,
                        recovery.Architecture,
                        "其他",
                        recovery.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        recovery.ErrorType ?? string.Empty,
                        recovery.ErrorCode ?? string.Empty));
            };
            prompt.Dismissed += (_, _) =>
            {
                if (handled)
                    return;
                handled = true;
                _crashRecoveryTracker.MarkDismissed();
                prompt.Close();
            };
            prompt.DoNotPromptAgainRequested += (_, _) =>
            {
                if (handled)
                    return;
                handled = true;
                _settings.CrashFeedbackPromptEnabled = false;
                ConfigManager.Save(_settings);
                _crashRecoveryTracker.MarkDismissed();
                prompt.Close();
            };
            prompt.Show();
        }
        catch
        {
            _crashRecoveryTracker.MarkDismissed();
            _crashRecoveryPromptWindow = null;
        }
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

    /// <summary>
    /// 托盘菜单"检查更新" — 手动检查，发现新版直接弹出更新对话框
    /// </summary>
    private void OnUpdateRequested()
    {
        Dispatcher.BeginInvoke(async () =>
        {
            var result = await UpdateService.CheckAsync(autoShowUpdateForm: true);
            switch (result.Outcome)
            {
                case UpdateCheckOutcome.UpToDate:
                    MessageBox.Show("当前已是最新版本。", "QuickTranslate 更新",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case UpdateCheckOutcome.Error:
                    MessageBox.Show("检查更新失败，请确认网络连接后重试。", "QuickTranslate 更新",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                case UpdateCheckOutcome.Timeout:
                    MessageBox.Show("更新检查长时间无响应，请稍后重试。", "QuickTranslate 更新",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                    // UpdateAvailable: UpdateService 已弹出更新对话框
                    // Skipped: 已有检查在进行或更新窗口已打开，无需打扰
            }
        });
    }

    /// <summary>
    /// 启动后延迟做一次静默更新检查。
    /// 发现新版时只弹托盘气泡，不抢焦点；用户点击气泡才进入更新流程。
    /// </summary>
    private void ScheduleStartupUpdateCheck(int delaySeconds)
    {
        Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ContinueWith(_ =>
        {
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        var result = await UpdateService.CheckAsync(autoShowUpdateForm: false);
                        if (result.Outcome == UpdateCheckOutcome.UpdateAvailable)
                        {
                            _trayIcon?.ShowBalloonTip(
                                "QuickTranslate 更新",
                                $"发现新版本 {result.NewVersion}，点击查看",
                                System.Windows.Forms.ToolTipIcon.Info,
                                duration: 8000);
                        }
                        // 其余结果静默处理，UpdateService 已记录日志
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Update", "update.startup_check_failed",
                            new { error_type = ex.GetType().Name });
                    }
                });
            }
            catch { /* app may have exited */ }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// 用户点击了托盘气泡 — 目前气泡只用于更新提示，
    /// 无待处理的新版本时 ShowUpdateFormForLastCheck 会返回 false 并静默忽略。
    /// </summary>
    private void OnUpdateBalloonClicked()
    {
        Dispatcher.BeginInvoke(() => UpdateService.ShowUpdateFormForLastCheck());
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

    private static WordLookupProviderSettings CreateWordLookupSettings(AppSettings settings) => new(
        settings.ApiBaseUrl,
        settings.ApiKey,
        settings.ModelName,
        settings.TargetLanguage,
        ProviderRequestPolicy.ResolveThinkingRequestValue(
            settings.ApiBaseUrl,
            settings.ModelName,
            settings.ThinkingMode));

    private LocalDictionaryWordLookupService? TryCreateLocalDictionaryWordLookupService()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "word-dictionary.db"),
            Path.Combine(AppContext.BaseDirectory, "word-dictionary.db")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            Logger.Info("App", "word_lookup.local_dictionary_disabled", new { });
            return null;
        }

        Logger.Info("App", "word_lookup.local_dictionary_enabled", new { });
        return new LocalDictionaryWordLookupService(path);
    }

    private void OnLookupClickStarted(PhysicalPoint anchor)
    {
        var snapshot = _trayClicks.RecordLeftButtonDown(
            Volatile.Read(ref _lookupVisible) == 1,
            anchor);
        Interlocked.Exchange(ref _pendingTrayClickSequence, snapshot.Sequence);
    }

    private void OnLookupSingleClickConfirmed()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _lookupDeactivationTimer.Stop();
            ApplyTrayClickAction(
                _trayClicks.ConfirmSingleClick(Interlocked.Read(ref _pendingTrayClickSequence)));
        });
    }

    private void OnLookupDoubleClick()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _lookupDeactivationTimer.Stop();
            ApplyTrayClickAction(_trayClicks.RecordDoubleClick());
        });
    }

    private void OnLookupDeactivated()
    {
        ApplyTrayClickAction(_trayClicks.RecordDeactivated());
        _lookupDeactivationTimer.Stop();
        _lookupDeactivationTimer.Start();
    }

    private void ApplyTrayClickAction(TrayClickAction action)
    {
        switch (action.Kind)
        {
            case TrayClickActionKind.ShowLookup when action.Snapshot is not null:
                ShowQuickLookup(action.Snapshot.Anchor);
                break;
            case TrayClickActionKind.HideLookup:
            case TrayClickActionKind.HideForDeactivation:
                _quickLookupWindow?.HidePanel();
                break;
            case TrayClickActionKind.OpenSettings:
                _quickLookupWindow?.HidePanel();
                OnSettingsRequested();
                break;
        }
    }

    private void ShowQuickLookup(PhysicalPoint anchor)
    {
        if (_quickLookupWindow is null)
            return;

        var physicalAnchor = new Point(anchor.X, anchor.Y);
        var work = Win32Api.GetPhysicalWorkAreaAtPoint(physicalAnchor);
        if (work.IsEmpty)
            return;
        var scale = DpiHelper.GetScaleForPhysicalPoint(physicalAnchor);
        var size = new PhysicalSize(
            (int)Math.Round(_quickLookupWindow.Width * scale.X),
            (int)Math.Round(_quickLookupWindow.Height * scale.Y));
        var rect = TrayPanelPlacement.Calculate(
            new PhysicalRect(
                (int)work.Left,
                (int)work.Top,
                (int)work.Width,
                (int)work.Height),
            anchor,
            size,
            scale.X * 96,
            scale.Y * 96);

        _quickLookupWindow.PrepareForShow();
        Volatile.Write(ref _lookupVisible, 1);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(_quickLookupWindow).Handle;
        Win32Api.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            0x0004);
        _quickLookupWindow.Activate();
    }

    /// <summary>
    /// 暂停/恢复翻译
    /// </summary>
    private void OnPauseToggled(bool isPaused)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnPauseToggled(isPaused)));
            return;
        }

        if (_settings is null)
            return;

        if (isPaused)
        {
            var paused = TranslationTriggerModes.Pause(
                _settings.TranslationTriggerMode,
                _settings.LastActiveTranslationTriggerMode);
            _settings.TranslationTriggerMode = paused.Mode;
            _settings.LastActiveTranslationTriggerMode = paused.LastActive;
            CancelActiveTranslationRequest();
            OnSelectionCancelled();
        }
        else
        {
            _settings.TranslationTriggerMode = TranslationTriggerModes.Resume(
                _settings.LastActiveTranslationTriggerMode);
        }

        ConfigManager.Save(_settings);
        ApplyHotKeyConfiguration(_settings);
        _trayIcon?.SetPaused(TranslationTriggerModes.IsPaused(_settings.TranslationTriggerMode));
        UpdateTrayToolTip();
    }

    /// <summary>
    /// 退出应用
    /// </summary>
    private void OnExitRequested()
    {
        var dispatcherAccess = Dispatcher.CheckAccess();
        var threadId = Environment.CurrentManagedThreadId;
        Logger.Info("App", "tray.exit.requested", new
        {
            dispatcher_access = dispatcherAccess,
            thread_id = threadId
        });
        Logger.WriteShutdownTrace(
            "tray.exit.requested",
            $"dispatcher_access={dispatcherAccess} thread_id={threadId}");

        // Hide the tray icon immediately on the menu click thread. Full Dispose
        // still runs at the start of OnExit on the WPF dispatcher; Hide is
        // idempotent so the icon does not linger during TTS/hook cleanup.
        try { _trayIcon?.Hide(); }
        catch { /* best-effort */ }

        // Tray menu is WinForms; always hop to the WPF dispatcher for shutdown.
        if (dispatcherAccess)
            Shutdown();
        else
            Dispatcher.BeginInvoke(new Action(Shutdown));
    }

    /// <summary>
    /// 更新托盘提示文本（显示当前状态）
    /// </summary>
    private void UpdateTrayToolTip()
    {
        if (_trayIcon == null || _settings == null) return;
        var status = TranslationTriggerModes.GetTrayStatusText(_settings.TranslationTriggerMode);
        _trayIcon.UpdateToolTip($"QuickTranslate - {status}");
    }

    private void ApplyHotKeyConfiguration(AppSettings settings)
    {
        if (_keyboardHook is null)
            return;

        var shouldEnableHotKey = TranslationTriggerModes.CanTriggerHotKey(settings.TranslationTriggerMode);
        Task.Run(() =>
        {
            _keyboardHook.Stop();
            _keyboardHook.HotKey = settings.HotKeyVK;
            _keyboardHook.RequireAlt = settings.HotKeyRequireAlt;
            _keyboardHook.RequireCtrl = settings.HotKeyRequireCtrl;
            _keyboardHook.RequireShift = settings.HotKeyRequireShift;
            if (shouldEnableHotKey)
                _keyboardHook.Start();
        });
    }

    /// <summary>
    /// 应用快速查词热键配置（独立于翻译热键，不受暂停影响）。
    /// </summary>
    private void ApplyQuickLookupHotKeyConfiguration(AppSettings settings)
    {
        if (_quickLookupHook is null)
            return;

        Task.Run(() =>
        {
            _quickLookupHook.Stop();
            _quickLookupHook.HotKey = settings.QuickLookupHotKeyVK;
            _quickLookupHook.RequireAlt = settings.QuickLookupHotKeyRequireAlt;
            _quickLookupHook.RequireCtrl = settings.QuickLookupHotKeyRequireCtrl;
            _quickLookupHook.RequireShift = settings.QuickLookupHotKeyRequireShift;
            if (settings.QuickLookupHotKeyEnabled)
                _quickLookupHook.Start();
        });
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
        _isExiting = true;
        var onExitWatch = Stopwatch.StartNew();
        var threadId = Environment.CurrentManagedThreadId;
        var hasTts = _ttsService is not null;
        Logger.Info("App", "app.onexit.begin", new
        {
            thread_id = threadId,
            has_tts = hasTts
        });
        Logger.WriteShutdownTrace(
            "app.onexit.begin",
            $"thread_id={threadId} has_tts={hasTts}");

        CancelActiveTranslationRequest();
        _lookupSessions.CancelCurrent();
        _lookupDeactivationTimer.Stop();
        _screenshotWindow?.CancelSelection();
        _screenshotWindow = null;
        _screenshotTranslationCts?.Cancel();
        _screenshotTranslationCts?.Dispose();
        _screenshotTranslationCts = null;
        _screenshotOverlayWindow?.Close();
        _screenshotOverlayWindow = null;
        _screenshotProgressWindow?.Close();
        _screenshotProgressWindow = null;
        Interlocked.Increment(ref _selectionGeneration);
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = null;
        ClipboardHelper.Dispose();
        // 停止看门狗
        _watchdogTimer?.Dispose();

        // Prefer tray teardown before slow work so the icon never outlives the
        // user-visible exit click (also covers non-tray Shutdown paths).
        try { _trayIcon?.Dispose(); }
        catch { /* best-effort */ }
        _trayIcon = null;

        _quickLookupWindow?.CloseForExit();
        _quickLookupWindow = null;
        _openAiWordLookupService?.Dispose();
        _openAiWordLookupService = null;
        _localWordLookupService = null;
        _wordLookupService = null;
        _lookupSessions.Dispose();
        _trayClicks.Dispose();

        // 清理资源
        // NOTE: This runs on the WPF UI thread. EdgeTtsService must not post+wait
        // on the same dispatcher here (CheckAccess inline path), or exit deadlocks.
        _ttsPlayback?.Dispose();
        _ttsPlayback = null;
        if (_ttsService is not null)
        {
            var disposeWatch = Stopwatch.StartNew();
            var disposeThreadId = Environment.CurrentManagedThreadId;
            var disposeDispatcherAccess = Dispatcher.CheckAccess();
            Logger.Info("App", "tts.dispose.begin", new
            {
                thread_id = disposeThreadId,
                dispatcher_access = disposeDispatcherAccess
            });
            Logger.WriteShutdownTrace(
                "tts.dispose.begin",
                $"thread_id={disposeThreadId} dispatcher_access={disposeDispatcherAccess}");

            try
            {
                _ttsService.DisposeAsync().AsTask().GetAwaiter().GetResult();
                disposeWatch.Stop();
                Logger.Info("App", "tts.dispose.end", new
                {
                    duration_ms = disposeWatch.Elapsed.TotalMilliseconds,
                    thread_id = disposeThreadId
                });
                Logger.WriteShutdownTrace(
                    "tts.dispose.end",
                    $"duration_ms={disposeWatch.Elapsed.TotalMilliseconds:F1} thread_id={disposeThreadId}");
            }
            catch (Exception ex)
            {
                disposeWatch.Stop();
                Logger.Warn("App", "tts.dispose.failed", new
                {
                    duration_ms = disposeWatch.Elapsed.TotalMilliseconds,
                    exception_type = ex.GetType().Name
                });
                Logger.WriteShutdownTrace(
                    "tts.dispose.failed",
                    $"duration_ms={disposeWatch.Elapsed.TotalMilliseconds:F1} exception_type={ex.GetType().Name}");
            }

            _ttsService = null;
        }
        _keyboardHook?.Dispose();
        _quickLookupHook?.Dispose();
        _selectionDetector?.Dispose();
        if (_screenshotOcrService is IDisposable screenshotOcrDisposable)
            screenshotOcrDisposable.Dispose();
        _screenshotOcrService = null;
        _screenshotTranslationCoordinator = null;
        _translationService?.Dispose();
        _dbContext?.Dispose();

        // 释放单实例 Mutex
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }

        onExitWatch.Stop();
        Logger.Info("App", "app.onexit.complete", new
        {
            duration_ms = onExitWatch.Elapsed.TotalMilliseconds
        });
        Logger.WriteShutdownTrace(
            "app.onexit.complete",
            $"duration_ms={onExitWatch.Elapsed.TotalMilliseconds:F1}");
        _crashRecoveryTracker?.MarkClean();
        Logger.Shutdown();
        base.OnExit(e);
    }
}

