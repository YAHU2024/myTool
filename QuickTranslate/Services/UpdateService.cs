using System;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AutoUpdaterDotNET;
using QuickTranslate.Helpers;

namespace QuickTranslate.Services;

/// <summary>
/// 更新检查结果
/// </summary>
public enum UpdateCheckOutcome
{
    /// <summary>已是最新版本</summary>
    UpToDate,
    /// <summary>发现新版本</summary>
    UpdateAvailable,
    /// <summary>检查失败（网络错误、解析失败、超时等）</summary>
    Error,
    /// <summary>上一次检查尚未完成，本次被跳过</summary>
    Skipped
}

/// <summary>
/// 一次更新检查的结果。<see cref="NewVersion"/> 仅在
/// <see cref="UpdateCheckOutcome.UpdateAvailable"/> 时有值。
/// </summary>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, string? NewVersion);

/// <summary>
/// 应用自动更新服务，基于 AutoUpdater.NET 实现。
/// 检查 GitHub Releases 上的 version.xml 获取最新版本信息，
/// 发现新版后提示用户下载并静默安装。
/// </summary>
/// <remarks>
/// 注意：订阅 AutoUpdater.CheckForUpdateEvent 会<b>抑制</b>库自带的更新对话框，
/// 由订阅方全权负责 UI。因此发现新版后必须显式调用 <c>AutoUpdater.ShowUpdateForm</c>，
/// 否则不会有任何界面出现。<see cref="AutoUpdater.RunUpdateAsAdmin"/> 等配置项
/// 也只有在该对话框被显示后才会生效。
/// </remarks>
public static class UpdateService
{
    /// <summary>
    /// 更新信息 XML 地址（GitHub Releases 永久重定向到最新版本）
    /// </summary>
    private const string UpdateXmlUrl =
        "https://github.com/YAHU2024/myTool/releases/latest/download/version.xml";

    /// <summary>
    /// 单次检查的超时兜底。AutoUpdater 的回调若因故未触发，
    /// 到时强制结束本次检查，避免后续检查被永久判为 Skipped。
    /// </summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(60);

    private static bool _configured;

    /// <summary>当前在飞的检查；为 null 表示空闲。仅在 UI 线程读写。</summary>
    private static TaskCompletionSource<UpdateCheckResult>? _pending;

    /// <summary>本次检查是否由调用方要求直接弹出更新对话框</summary>
    private static bool _autoShowUpdateForm;

    /// <summary>最近一次检查到的可用更新，供托盘气泡点击后复用</summary>
    private static UpdateInfoEventArgs? _lastAvailable;

    private static DispatcherTimer? _timeoutTimer;
    private static long _checkStartedAt;

    /// <summary>
    /// 一次性配置 AutoUpdater 全局设置和事件订阅
    /// </summary>
    private static void Configure()
    {
        if (_configured) return;
        _configured = true;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
        {
            AutoUpdater.InstalledVersion = version;
        }

        // 以下四项只作用于 ShowUpdateForm 弹出的标准对话框及其下载流程，
        // 不调用 ShowUpdateForm 时它们不会有任何效果。

        // 以管理员权限运行安装程序（Inno Setup 需要写入 Program Files）
        AutoUpdater.RunUpdateAsAdmin = true;

        // 不显示"跳过此版本"按钮（小工具无需跳过）
        AutoUpdater.ShowSkipButton = false;

        // 显示"稍后提醒"按钮
        AutoUpdater.ShowRemindLaterButton = true;

        // 发现新版后直接下载（不跳转浏览器）
        AutoUpdater.OpenDownloadPage = false;

        // 使用系统代理（Clash/V2Ray 等通过系统代理转发流量）
        // 不设置此项时，AutoUpdater.NET 可能直连 GitHub 超时（国内网络）
        AutoUpdater.Proxy = WebRequest.GetSystemWebProxy();

        // 订阅后库自带的更新/错误对话框全部被抑制，改由本类通过
        // args.Error / args.IsUpdateAvailable 自行决定 UI，因此不设置
        // AutoUpdater.ReportErrors —— 它在订阅路径下不生效，留着只会误导。
        AutoUpdater.CheckForUpdateEvent += OnCheckForUpdate;

        // 订阅应用退出事件（更新安装前优雅退出）
        AutoUpdater.ApplicationExitEvent += OnApplicationExit;
    }

    /// <summary>
    /// 执行一次更新检查。结果只返回给本次调用方，不会广播。
    /// </summary>
    /// <param name="autoShowUpdateForm">
    /// 发现新版时是否立即弹出更新对话框。手动检查传 true；
    /// 启动时的静默检查传 false，由调用方改用托盘气泡提示，
    /// 用户点击气泡后再调用 <see cref="ShowUpdateFormForLastCheck"/>。
    /// </param>
    /// <remarks>
    /// 必须在 UI 线程调用：AutoUpdater.Start 与 DispatcherTimer 均有线程亲和性。
    /// </remarks>
    public static Task<UpdateCheckResult> CheckAsync(bool autoShowUpdateForm)
    {
        // 已有检查在飞：直接返回 Skipped，不触碰在飞请求的 TCS，
        // 它的真实结果照常回到它自己的 awaiter。
        if (_pending is not null)
        {
            return Task.FromResult(new UpdateCheckResult(UpdateCheckOutcome.Skipped, null));
        }

        var tcs = new TaskCompletionSource<UpdateCheckResult>();
        _pending = tcs;
        _autoShowUpdateForm = autoShowUpdateForm;
        _checkStartedAt = Stopwatch.GetTimestamp();

        try
        {
            Configure();
            StartTimeoutTimer();
            AutoUpdater.Start(UpdateXmlUrl);
            Logger.Info("Update", "update.check_started", new { manual = autoShowUpdateForm });
        }
        catch (Exception ex)
        {
            StopTimeoutTimer();
            _pending = null;
            Logger.Warn("Update", "update.check_start_failed",
                new { error_type = ex.GetType().Name, manual = autoShowUpdateForm });
            tcs.TrySetResult(new UpdateCheckResult(UpdateCheckOutcome.Error, null));
        }

        return tcs.Task;
    }

    /// <summary>
    /// 弹出最近一次检查发现的更新对话框（供托盘气泡点击后调用）。
    /// 无待处理的新版本时返回 false。
    /// </summary>
    public static bool ShowUpdateFormForLastCheck()
    {
        var args = _lastAvailable;
        if (args is null) return false;

        AutoUpdater.ShowUpdateForm(args);
        return true;
    }

    /// <summary>
    /// 更新检查完成回调 — 记录诊断日志、回传结果，必要时弹出更新对话框
    /// </summary>
    private static void OnCheckForUpdate(UpdateInfoEventArgs args)
    {
        StopTimeoutTimer();

        // 取出并清空在飞状态。可能为 null（已超时兜底结束），
        // 此时仍要更新 _lastAvailable，但不再回传结果、不弹窗。
        var tcs = _pending;
        _pending = null;
        var autoShow = _autoShowUpdateForm;
        var duration = Stopwatch.GetElapsedTime(_checkStartedAt);

        if (args.Error is not null)
        {
            Logger.Warn("Update", "update.check_error", new
            {
                error_type = args.Error.GetType().Name,
                manual = autoShow,
                late = tcs is null,
                duration_ms = duration.TotalMilliseconds
            });
            tcs?.TrySetResult(new UpdateCheckResult(UpdateCheckOutcome.Error, null));
            return;
        }

        if (args.IsUpdateAvailable)
        {
            _lastAvailable = args;
            Logger.Info("Update", "update.available", new
            {
                installed = args.InstalledVersion?.ToString(),
                current = args.CurrentVersion?.ToString(),
                mandatory = args.Mandatory,
                manual = autoShow,
                late = tcs is null,
                duration_ms = duration.TotalMilliseconds
            });

            // 先回传结果再弹窗：ShowUpdateForm 是模态阻塞的，而 TCS 默认
            // 同步执行延续，这个顺序能让调用方的界面（如设置页状态文字）
            // 在对话框弹出之前就完成刷新。
            tcs?.TrySetResult(new UpdateCheckResult(
                UpdateCheckOutcome.UpdateAvailable, args.CurrentVersion?.ToString()));

            if (tcs is not null && autoShow)
            {
                AutoUpdater.ShowUpdateForm(args);
            }
            return;
        }

        _lastAvailable = null;
        Logger.Info("Update", "update.up_to_date", new
        {
            installed = args.InstalledVersion?.ToString(),
            manual = autoShow,
            late = tcs is null,
            duration_ms = duration.TotalMilliseconds
        });
        tcs?.TrySetResult(new UpdateCheckResult(UpdateCheckOutcome.UpToDate, null));
    }

    /// <summary>
    /// 启动超时兜底计时器。到时强制结束本次检查并释放在飞状态，
    /// 避免回调不触发时后续检查被永久判为 Skipped。
    /// </summary>
    private static void StartTimeoutTimer()
    {
        StopTimeoutTimer();

        _timeoutTimer = new DispatcherTimer { Interval = CheckTimeout };
        _timeoutTimer.Tick += (_, _) =>
        {
            StopTimeoutTimer();

            var tcs = _pending;
            if (tcs is null) return;
            _pending = null;

            Logger.Warn("Update", "update.check_timeout",
                new { manual = _autoShowUpdateForm, timeout_s = CheckTimeout.TotalSeconds });
            tcs.TrySetResult(new UpdateCheckResult(UpdateCheckOutcome.Error, null));
        };
        _timeoutTimer.Start();
    }

    private static void StopTimeoutTimer()
    {
        _timeoutTimer?.Stop();
        _timeoutTimer = null;
    }

    /// <summary>
    /// 应用即将退出以执行更新安装。
    /// </summary>
    /// <remarks>
    /// 这里<b>无法</b>保证进程在安装程序启动前终止：AutoUpdater.NET 是先
    /// Process.Start 拉起安装程序、再触发本回调的；而 Application.Shutdown()
    /// 只是向 Dispatcher 队列投递关闭请求，本身不阻塞（外层的 Dispatcher.Invoke
    /// 只同步了封送，不同步关闭本身）。真正确保文件不被占用的是 Inno Setup 的
    /// CloseApplications=force（见 installer/QuickTranslate-setup.iss），
    /// 它通过 Restart Manager 强制关闭本进程。
    ///
    /// 仍调用 Shutdown() 的价值在于走正常的 App.OnExit 清理路径
    /// （Logger 落盘、托盘图标释放、单实例 Mutex 释放），
    /// 比被 Restart Manager 直接杀掉更干净。
    /// 不调用 Logger.Shutdown()（由 App.OnExit 统一处理）。
    /// </remarks>
    private static void OnApplicationExit()
    {
        Logger.Info("Update", "update.installing_exit");
        Logger.WriteShutdownTrace("update.installing_exit", "AutoUpdater triggered app exit for update");

        if (Application.Current?.Dispatcher.CheckAccess() == true)
        {
            Application.Current.Shutdown();
        }
        else
        {
            Application.Current?.Dispatcher.Invoke(() =>
                Application.Current.Shutdown());
        }
    }
}
