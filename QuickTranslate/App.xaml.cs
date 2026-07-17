using System;
using System.Diagnostics;
using System.Windows;
using QuickTranslate.Core;
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
    private bool _isTranslating;
    private string? _pendingText; // 待翻译文本（红点悬停时使用）

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 加载配置
        _settings = ConfigManager.Load();

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
        _keyboardHook.HotKeyPressed += OnHotKeyPressed;
        _keyboardHook.Start();

        // 启动文本选择检测器
        _selectionDetector = new SelectionDetector();
        _selectionDetector.SelectionCompleted += OnSelectionCompleted;
        _selectionDetector.ClickedOutside += OnSelectionCancelled;
        _selectionDetector.Start();

        // 显示主窗口
        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    /// <summary>
    /// 热键事件处理（默认 Alt+Q）
    /// </summary>
    private async void OnHotKeyPressed()
    {
        if (_isTranslating || _translationService == null || _settings == null || _floatingWindow == null)
            return;

        _isTranslating = true;

        try
        {
            // 尝试 UIA 获取选中文本位置，降级为鼠标位置
            var anchorPosition = GetSelectionAnchorPosition();

            // 先显示悬浮窗，提示正在翻译
            _floatingWindow.SetSource("正在获取...");
            _floatingWindow.UpdateTranslation("翻译中...");
            _floatingWindow.ShowTranslation("正在获取...", "翻译中...", anchorPosition);

            // 获取选中文本
            var selectedText = await ClipboardHelper.GetSelectedTextAsync();

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                _floatingWindow.SetSource("未检测到选中文本");
                _floatingWindow.UpdateTranslation("请先选中要翻译的文本");
                return;
            }

            // 更新悬浮窗显示原文
            _floatingWindow.SetSource(selectedText);
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

            Debug.WriteLine($"翻译完成: {result.Length} 字");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"翻译出错: {ex.Message}");
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
        if (_redDotWindow == null) return;

        // 尝试获取选中文本
        var selectedText = await ClipboardHelper.GetSelectedTextAsync();

        if (string.IsNullOrWhiteSpace(selectedText))
        {
            _redDotWindow.Hide();
            _selectionDetector!.IsRedDotVisible = false;
            return;
        }

        // 保存待翻译文本
        _pendingText = selectedText;

        // 尝试 UIA 精确定位
        var location = SelectionLocator.TryGetSelectionBounds();
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

    /// <summary>
    /// 红点悬停事件处理 - 触发翻译
    /// </summary>
    private async void OnRedDotHovered()
    {
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
            _floatingWindow.SetSource(textToTranslate);
            _floatingWindow.UpdateTranslation("翻译中...");
            _floatingWindow.ShowTranslation(textToTranslate, "翻译中...", dotPosition);

            // 流式翻译
            var targetLang = _settings.TargetLanguage;
            var result = await _translationService.TranslateStreamingAsync(
                textToTranslate,
                targetLang,
                chunk => Dispatcher.Invoke(() =>
                {
                    _floatingWindow.UpdateTranslation(chunk);
                }));

            Debug.WriteLine($"翻译完成: {result.Length} 字");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"翻译出错: {ex.Message}");
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
    /// 获取选中文本锚点位置（优先 UIA，降级为鼠标位置）
    /// </summary>
    private System.Windows.Point GetSelectionAnchorPosition()
    {
        var location = SelectionLocator.TryGetSelectionBounds();
        if (location != null && location.IsValid)
            return location.EndPoint;

        // 降级为当前鼠标位置
        Win32Api.GetCursorPos(out var cursorPoint);
        return new System.Windows.Point(cursorPoint.X, cursorPoint.Y);
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

    protected override void OnExit(ExitEventArgs e)
    {
        // 清理钩子资源
        _keyboardHook?.Dispose();
        _selectionDetector?.Dispose();
        base.OnExit(e);
    }
}

