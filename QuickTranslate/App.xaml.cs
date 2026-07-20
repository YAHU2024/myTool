using System;
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
    private string? _pendingText; // 待翻译文本（红点悬停时使用）

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 加载配置
        _settings = ConfigManager.Load();

        // 初始化日志系统
#if DEBUG
        // WinExe 无控制台，附加到父进程终端以实时输出日志
        Win32Api.AttachConsole(Win32Api.ATTACH_PARENT_PROCESS);
#endif
        Logger.Init(
            minLevel: Logger.ParseLevel(_settings.LogLevel),
            retentionDays: _settings.LogRetentionDays);
        Logger.Info("App", $"应用启动, OS={Environment.OSVersion}, .NET={Environment.Version}");

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
        _keyboardHook.Start();

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
        _trayIcon.ExitRequested += OnExitRequested;

        // 根据配置更新托盘提示
        UpdateTrayToolTip();

        // 启动后直接最小化到托盘，不显示主窗口
        // MainWindow 不创建、不显示
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

        _isTranslating = true;

        try
        {
            // 尝试 UIA 异步获取选中文本位置，降级为鼠标位置
            var anchorPosition = await GetSelectionAnchorPositionAsync();

            // 先显示悬浮窗，提示正在翻译
            _floatingWindow.ShowTranslation("翻译中...", anchorPosition);

            // 获取选中文本
            var selectedText = await ClipboardHelper.GetSelectedTextAsync();

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                _floatingWindow.UpdateTranslation("请先选中要翻译的文本");
                return;
            }

            // 更新悬浮窗提示
            _floatingWindow.UpdateTranslation("翻译中...");

            // 流式翻译
            var targetLang = _settings.TargetLanguage;
            var result = await _translationService.TranslateStreamingAsync(
                selectedText,
                targetLang,
                chunk => Dispatcher.Invoke(() =>
                {
                    _floatingWindow.UpdateTranslation(chunk);
                }));

            // 保存翻译历史
            SaveTranslationHistory(selectedText, result, targetLang);

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
    /// 文本选择完成事件处理 - 显示红点
    /// </summary>
    private async void OnSelectionCompleted(System.Windows.Point startPos, System.Windows.Point endPos)
    {
        try
        {
            if (!IsTranslationEnabled) return;
            if (_redDotWindow == null) return;

            // 尝试获取选中文本（UIA 在后台 STA 线程执行，不阻塞 UI 线程）
            var selectedText = await ClipboardHelper.GetSelectedTextAsync();

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                _redDotWindow.Hide();
                _selectionDetector!.IsRedDotVisible = false;
                return;
            }

            // 保存待翻译文本
            _pendingText = selectedText;

            // 尝试 UIA 异步精确定位（不阻塞 UI 线程）
            var location = await SelectionLocator.TryGetSelectionBoundsAsync();
            if (location == null || !location.IsValid)
            {
                // 降级：拖拽中点 + 偏移(+4, -8)
                var mid = new System.Windows.Point(
                    (startPos.X + endPos.X) / 2 + 4,
                    (startPos.Y + endPos.Y) / 2 - 8);
                location = new SelectionLocation
                {
                    IsValid = false,
                    FallbackPoint = mid
                };
            }

            // 显示红点
            _redDotWindow.ShowAt(location);
            _selectionDetector!.IsRedDotVisible = true;
        }
        catch (Exception ex)
        {
            Logger.Error("App", "OnSelectionCompleted 异常", ex);
        }
    }

    /// <summary>
    /// 红点悬停事件处理 - 触发翻译
    /// </summary>
    private async void OnRedDotHovered()
    {
        if (!IsTranslationEnabled) return;
        if (_isTranslating || _translationService == null || _settings == null || _floatingWindow == null)
            return;

        if (string.IsNullOrWhiteSpace(_pendingText)) return;

        // 标记红点已隐藏
        _selectionDetector!.IsRedDotVisible = false;
        _redDotWindow?.Hide();

        _isTranslating = true;
        var textToTranslate = _pendingText;
        _pendingText = null;

        try
        {
            // 使用红点位置（而非鼠标位置）作为悬浮窗显示位置
            var dotPosition = _redDotWindow?.DotScreenPosition
                ?? new System.Windows.Point(0, 0);

            // 显示悬浮窗
            _floatingWindow.ShowTranslation("翻译中...", dotPosition);

            // 流式翻译
            var targetLang = _settings.TargetLanguage;
            var result = await _translationService.TranslateStreamingAsync(
                textToTranslate,
                targetLang,
                chunk => Dispatcher.Invoke(() =>
                {
                    _floatingWindow.UpdateTranslation(chunk);
                }));

            // 保存翻译历史
            SaveTranslationHistory(textToTranslate, result, targetLang);

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
        _selectionDetector!.IsRedDotVisible = false;
        _redDotWindow?.Hide();
        _pendingText = null;
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

        // 更新快捷键配置
        if (_keyboardHook != null)
        {
            _keyboardHook.Stop();
            _keyboardHook.HotKey = settings.HotKeyVK;
            _keyboardHook.RequireAlt = settings.HotKeyRequireAlt;
            _keyboardHook.RequireCtrl = settings.HotKeyRequireCtrl;
            _keyboardHook.RequireShift = settings.HotKeyRequireShift;
            _keyboardHook.Start();
        }

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
    private void SaveTranslationHistory(string sourceText, string translation, string targetLang)
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
        // 清理资源
        _keyboardHook?.Dispose();
        _selectionDetector?.Dispose();
        _trayIcon?.Dispose();
        _dbContext?.Dispose();

        Logger.Info("App", "应用退出");
        Logger.Shutdown();
        base.OnExit(e);
    }
}

