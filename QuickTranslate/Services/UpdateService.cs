using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using AutoUpdaterDotNET;
using QuickTranslate.Helpers;
using QuickTranslate.UI;

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
    /// <summary>检查失败（网络错误、解析失败等）</summary>
    Error,
    /// <summary>检查发出后迟迟没有回应，被超时兜底结束</summary>
    Timeout,
    /// <summary>上一次检查尚未完成，或更新窗口已排队/打开</summary>
    Skipped
}

/// <summary>
/// 一次更新检查的结果。<see cref="NewVersion"/> 仅在
/// <see cref="UpdateCheckOutcome.UpdateAvailable"/> 时有值。
/// </summary>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, string? NewVersion);

// ─── Adapter layer: wraps AutoUpdater.NET static calls for testability ──────

/// <summary>
/// 封装 AutoUpdater.NET 的静态调用，以便确定性测试超时和回调顺序。
/// </summary>
internal interface IAutoUpdaterAdapter
{
    /// <summary>启动一次版本检查。完成后触发 <see cref="CheckForUpdateCompleted"/>。</summary>
    void Start(string url);

    /// <summary>版本检查完成（AutoUpdater.CheckForUpdateEvent 的封装）。</summary>
    event EventHandler<UpdateInfoEventArgs>? CheckForUpdateCompleted;
}

/// <summary>
/// 生产环境适配器：直接委托给 AutoUpdater.NET 的静态方法，
/// 同时将 AutoUpdater 的自定义委托桥接到 EventHandler&lt;UpdateInfoEventArgs&gt;。
/// </summary>
internal sealed class AutoUpdaterAdapter : IAutoUpdaterAdapter
{
    private EventHandler<UpdateInfoEventArgs>? _handler;

    public void Start(string url)
    {
        AutoUpdater.Start(url);
    }

    public event EventHandler<UpdateInfoEventArgs>? CheckForUpdateCompleted
    {
        add
        {
            _handler += value;
            // AutoUpdater.CheckForUpdateEvent: delegate void CheckForUpdateEventHandler(UpdateInfoEventArgs)
            // Bridge to EventHandler<UpdateInfoEventArgs> (object? sender, UpdateInfoEventArgs e)
            AutoUpdater.CheckForUpdateEvent += OnCheckForUpdateBridge;
        }
        remove
        {
            _handler -= value;
            if (_handler is null)
                AutoUpdater.CheckForUpdateEvent -= OnCheckForUpdateBridge;
        }
    }

    private void OnCheckForUpdateBridge(UpdateInfoEventArgs args)
    {
        _handler?.Invoke(this, args);
    }
}

/// <summary>
/// 应用自动更新服务。
/// 版本检查使用 AutoUpdater.NET，下载和安装由本服务自行处理，
/// 以在安装包执行前插入 Authenticode 签名验证，建立独立信任链。
/// </summary>
/// <remarks>
/// <b>信任链设计</b>
/// <para>
/// 更新安装包需要管理员权限运行。仅靠 SHA256 校验和不够安全，
/// 因为校验和与安装包都由同一 GitHub Release 分发——攻击者取得
/// Release 发布能力后可同时替换两者。
/// </para>
/// <para>
/// Authenticode 代码签名提供独立信任链：
/// 签名私钥不进入仓库、CI 日志或构建产物；即使 GitHub Release 被
/// 完全控制，攻击者也无法伪造有效的 Authenticode 签名。
/// 应用在启动安装程序前依次验证：
/// <list type="number">
/// <item>SHA256 传输完整性</item>
/// <item>Authenticode 签名有效性 + 证书链可信</item>
/// <item>签名发布者与预期发布者一致</item>
/// </list>
/// 任一验证失败立即中止安装并显示明确错误，禁止降级继续执行。
/// </para>
/// </remarks>
public static class UpdateService
{
    /// <summary>
    /// 更新信息 XML 地址（GitHub Releases 永久重定向到最新版本）
    /// </summary>
    private const string UpdateXmlUrl =
        "https://github.com/YAHU2024/myTool/releases/latest/download/version.xml";

    /// <summary>
    /// 安装包下载与验证的总超时（5 分钟）。
    /// </summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 单次检查的超时兜底（60 秒）。
    /// </summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether Authenticode signature verification is enforced.
    /// Set from <see cref="Models.AppSettings.RequireAuthenticodeSignature"/>
    /// during application startup. When false (default), missing or invalid
    /// signatures only produce a warning log — SHA256 integrity still applies.
    /// </summary>
    public static bool RequireAuthenticodeSignature { get; set; }

    /// <summary>
    /// 预期的 Authenticode 签名发布者（证书 Subject 子串，大小写不敏感）。
    /// 此常量是独立信任链的锚点——签名证书必须包含此字符串。
    /// 证书续期时需在代码中更新此行并随新版本一起发布。
    /// </summary>
    private const string ExpectedPublisher = "YaHu";

    /// <summary>
    /// 共享 HttpClient，避免端口耗尽。
    /// </summary>
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = DownloadTimeout
    };

    private static bool _configured;

    private static readonly DeferredModalRunner UpdateFormRunner = new(action =>
    {
        Application.Current.Dispatcher.BeginInvoke(action);
    });

    /// <summary>AutoUpdater.NET 静态调用适配器（通过 SetAdapterForTesting 替换为假实现）</summary>
    private static IAutoUpdaterAdapter _adapter = new AutoUpdaterAdapter();

    /// <summary>单调递增的检查代次，用于日志关联和诊断。</summary>
    private static long _checkGeneration;

    /// <summary>
    /// 当前在飞的 TCS。超时后不会被清空——需要通过迟到回调或清理定时器来消费，
    /// 以阻止后续检查在上一轮回调到达前启动（隔离迟到竞态）。
    /// 仅在 UI 线程读写。
    /// </summary>
    private static TaskCompletionSource<UpdateCheckResult>? _pending;

    /// <summary>本次检查是否由调用方要求直接弹出更新对话框</summary>
    private static bool _autoShowUpdateForm;

    /// <summary>最近一次检查到的可用更新</summary>
    private static UpdateInfoEventArgs? _lastAvailable;

    /// <summary>下载/安装是否正在进行中</summary>
    private static bool _installInProgress;

    private static DispatcherTimer? _timeoutTimer;
    private static DispatcherTimer? _cleanupTimer;
    private static long _checkStartedAt;

    /// <summary>
    /// 超时后等待迟到回调的宽限期。
    /// 此值必须远超 AutoUpdater.NET 的 HTTP 超时，确保在清理 _pending 之前
    /// 迟到回调已经到达。宽限期到后才允许启动下一次检查。
    /// 10 分钟对于任何合理的 HTTP 请求都已足够。
    /// </summary>
    private static readonly TimeSpan LateCallbackGracePeriod = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 一次性配置 AutoUpdater 全局设置和事件订阅。
    /// 版本检查仍由 AutoUpdater 执行，但下载和安装由本服务接管。
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

        // 安装配置项仅在 ShowUpdateForm 被调用时生效。
        // 由于本服务接管了下载安装流程，以下配置在自行启动
        // Process.Start 时手动应用，此处保留作为后备。
        AutoUpdater.RunUpdateAsAdmin = true;
        AutoUpdater.ShowSkipButton = false;
        AutoUpdater.ShowRemindLaterButton = false;
        AutoUpdater.OpenDownloadPage = false;

        // 使用系统代理（Clash/V2Ray 等通过系统代理转发流量）
        AutoUpdater.Proxy = WebRequest.GetSystemWebProxy();

        // 禁用库的默认注册表持久化
        AutoUpdater.PersistenceProvider = NoOpUpdatePersistenceProvider.Instance;

        // 通过适配器订阅——生产适配器委托给 AutoUpdater，测试适配器可模拟
        _adapter.CheckForUpdateCompleted += (_, e) => OnCheckForUpdate(e);

        // 订阅应用退出事件（更新安装前优雅退出）
        AutoUpdater.ApplicationExitEvent += OnApplicationExit;
    }

    /// <summary>
    /// 执行一次更新检查。必须在 UI 线程调用。
    /// </summary>
    /// <param name="autoShowUpdateForm">
    /// 发现新版时是否立即启动下载安装流程。手动检查传 true；
    /// 静默检查传 false，由调用方用托盘气泡提示。
    /// </param>
    public static Task<UpdateCheckResult> CheckAsync(bool autoShowUpdateForm)
    {
        // 正在下载安装，拒绝新的检查
        if (_installInProgress)
        {
            return Task.FromResult(new UpdateCheckResult(UpdateCheckOutcome.Skipped, null));
        }

        // 已有检查在飞或等待迟到回调清理
        if (_pending is not null)
        {
            return Task.FromResult(new UpdateCheckResult(UpdateCheckOutcome.Skipped, null));
        }

        // 更新对话框已打开
        if (UpdateFormRunner.IsBusy)
        {
            Logger.Info("Update", "update.check_skipped_form_open");
            return Task.FromResult(new UpdateCheckResult(UpdateCheckOutcome.Skipped, null));
        }

        Configure();

        var gen = Interlocked.Increment(ref _checkGeneration);
        var tcs = new TaskCompletionSource<UpdateCheckResult>();
        _pending = tcs;
        _autoShowUpdateForm = autoShowUpdateForm;
        _checkStartedAt = Stopwatch.GetTimestamp();

        try
        {
            StartTimeoutTimer();
            _adapter.Start(UpdateXmlUrl);
            Logger.Info("Update", "update.check_started", new { gen, manual = autoShowUpdateForm });
        }
        catch (Exception ex)
        {
            StopTimeoutTimer();
            _pending = null;
            Logger.Warn("Update", "update.check_start_failed",
                new { gen, error_type = ex.GetType().Name, manual = autoShowUpdateForm });
            tcs.TrySetResult(new UpdateCheckResult(UpdateCheckOutcome.Error, null));
        }

        return tcs.Task;
    }

    /// <summary>
    /// 弹出最近一次检查发现的更新对话框（供托盘气泡点击后调用）。
    /// 展示版本信息和 changelog，用户确认后由 <see cref="DownloadAndInstallAsync"/> 接管下载安装流程。
    /// </summary>
    public static bool ShowUpdateFormForLastCheck()
    {
        var args = _lastAvailable;
        if (args is null) return false;

        if (UpdateFormRunner.IsBusy || _installInProgress) return false;

        return UpdateFormRunner.TryRun(() => ShowUpdateConfirmation(args, null));
    }

    /// <summary>
    /// 更新检查完成回调。
    /// 迟到回调（_pending 已被超时或上一轮消费）只会更新缓存状态，
    /// 不会触碰后续检查的 TCS、计时器或弹窗标志。
    /// </summary>
    private static void OnCheckForUpdate(UpdateInfoEventArgs args)
    {
        StopTimeoutTimer();
        StopCleanupTimer();

        var tcs = _pending;
        _pending = null;
        var autoShow = _autoShowUpdateForm;
        var gen = Interlocked.Read(ref _checkGeneration);
        var duration = Stopwatch.GetElapsedTime(_checkStartedAt);

        // tcs is null  => 迟到回调：TCS 已被此前的回调或超时消费
        // tcs.Task.IsCompleted => 此 TCS 已被超时提前完成——也是迟到回调
        var isLate = tcs is null || tcs.Task.IsCompleted;

        if (args.Error is not null)
        {
            var webEx = args.Error as WebException;
            Logger.Warn("Update", "update.check_error", new
            {
                gen,
                error_type = args.Error.GetType().Name,
                web_status = webEx?.Status.ToString(),
                http_status = (webEx?.Response as HttpWebResponse)?.StatusCode.ToString(),
                manual = autoShow,
                late = isLate,
                duration_ms = duration.TotalMilliseconds
            });

            if (!isLate)
                tcs?.TrySetResult(new UpdateCheckResult(UpdateCheckOutcome.Error, null));
            return;
        }

        if (args.IsUpdateAvailable)
        {
            // 始终更新缓存状态——迟到回调的更新信息仍有价值
            _lastAvailable = args;
            Logger.Info("Update", "update.available", new
            {
                gen,
                installed = args.InstalledVersion?.ToString(),
                current = args.CurrentVersion?.ToString(),
                mandatory = args.Mandatory,
                manual = autoShow,
                late = isLate,
                duration_ms = duration.TotalMilliseconds
            });

            // 迟到回调：只更新缓存，不触碰 TCS、不弹窗
            if (isLate) return;

            // 正常路径：先回传结果再启动下载
            tcs?.TrySetResult(new UpdateCheckResult(
                UpdateCheckOutcome.UpdateAvailable, args.CurrentVersion?.ToString()));

            if (autoShow)
            {
                // 先弹出确认对话框展示版本信息和 changelog，
                // 用户确认后由 DownloadAndInstallAsync 接管下载安装流程
                ShowUpdateConfirmation(args, null);
            }
            return;
        }

        _lastAvailable = null;
        Logger.Info("Update", "update.up_to_date", new
        {
            gen,
            installed = args.InstalledVersion?.ToString(),
            manual = autoShow,
            late = isLate,
            duration_ms = duration.TotalMilliseconds
        });

        if (!isLate)
            tcs?.TrySetResult(new UpdateCheckResult(UpdateCheckOutcome.UpToDate, null));
    }

    /// <summary>
    /// [仅测试] 替换确认对话框实现为无 WPF 依赖的委托。
    /// 返回 true 表示"用户确认更新"，返回 false 表示"用户取消"。
    /// 设置为 null 恢复生产环境的 WPF 对话框。
    /// </summary>
    internal static Func<UpdateInfoEventArgs, Window?, bool>? ShowConfirmDialogForTesting { get; set; }

    /// <summary>
    /// 弹出确认对话框，展示新版本信息和 changelog（内嵌 WebBrowser）。
    /// 用户点击"立即更新"后调用 <see cref="DownloadAndInstallAsync"/> 接管下载安装流程。
    /// </summary>
    private static void ShowUpdateConfirmation(UpdateInfoEventArgs args, Window? owner)
    {
        if (ShowConfirmDialogForTesting is not null)
        {
            if (ShowConfirmDialogForTesting(args, owner))
                _ = DownloadAndInstallAsync(args, owner);
            return;
        }

        var window = new UpdateAvailableWindow(
            args.InstalledVersion?.ToString(),
            args.CurrentVersion?.ToString(),
            args.ChangelogURL)
        {
            Mandatory = args.Mandatory.Value
        };

        if (owner is not null)
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();

        if (window.UpdateConfirmed)
        {
            _ = DownloadAndInstallAsync(args, owner);
        }
    }

    /// <summary>
    /// 下载安装包、验证 SHA256 和 Authenticode 签名，通过后启动安装程序。
    /// 这是独立信任链的核心实现。
    /// </summary>
    /// <param name="args">AutoUpdater 解析的更新信息。</param>
    /// <param name="owner">弹窗的父窗口，null 时居中于屏幕。</param>
    private static async Task DownloadAndInstallAsync(UpdateInfoEventArgs args, Window? owner)
    {
        if (_installInProgress) return;
        _installInProgress = true;

        var downloadUrl = args.DownloadURL;
        var installerArgs = args.InstallerArgs ?? "/SILENT /SUPPRESSMSGBOXES /NORESTART";
        var tempFile = Path.Combine(Path.GetTempPath(),
            $"QuickTranslate-Update-{Guid.NewGuid():N}.exe");

        var progressWindow = new DownloadUpdateWindow();
        if (owner is not null)
            progressWindow.Owner = owner;

        try
        {
            // Show progress window non-modally so we can update it
            progressWindow.Show();

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                ShowFailure(progressWindow, "下载地址无效");
                return;
            }

            // Fetch version.xml metadata: checksum + signer subject.
            // We parse version.xml ourselves because AutoUpdater.NET's
            // UpdateInfoEventArgs does not expose the checksum or
            // custom elements like <signer>.
            var metadata = await FetchVersionMetadataAsync(progressWindow.CancellationToken);

            // ─── Phase 1: Download ──────────────────────────────────

            Logger.Info("Update", "update.download_started",
                new { url = downloadUrl, size_hint = "unknown" });

            progressWindow.ReportProgress(0, "正在连接服务器...");

            using var response = await HttpClient.GetAsync(
                downloadUrl, HttpCompletionOption.ResponseHeadersRead,
                progressWindow.CancellationToken);

            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            Logger.Info("Update", "update.download_connected",
                new { status = (int)response.StatusCode, total_bytes = totalBytes });

            await using var contentStream = await response.Content
                .ReadAsStreamAsync(progressWindow.CancellationToken);
            await using var fileStream = new FileStream(
                tempFile, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 81920, useAsync: true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            var lastReport = Stopwatch.GetTimestamp();

            while ((bytesRead = await contentStream.ReadAsync(
                       buffer, progressWindow.CancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead),
                    progressWindow.CancellationToken);
                totalRead += bytesRead;

                // Throttle UI updates to ~10 Hz
                var elapsed = Stopwatch.GetElapsedTime(lastReport);
                if (elapsed.TotalMilliseconds >= 100 || totalRead == totalBytes)
                {
                    if (totalBytes > 0)
                    {
                        var pct = (int)(totalRead * 100L / totalBytes);
                        progressWindow.ReportProgress(pct,
                            $"正在下载... {FormatBytes(totalRead)} / {FormatBytes(totalBytes)}");
                    }
                    else
                    {
                        progressWindow.ReportProgress(-1,
                            $"正在下载... {FormatBytes(totalRead)}");
                    }
                    lastReport = Stopwatch.GetTimestamp();
                }
            }

            await fileStream.FlushAsync(progressWindow.CancellationToken);
            fileStream.Close();

            Logger.Info("Update", "update.download_complete",
                new { total_bytes = totalRead, file = tempFile });

            // ─── Phase 2: SHA256 verification ───────────────────────

            progressWindow.ReportProgress(100, "正在验证文件完整性...");

            if (string.IsNullOrWhiteSpace(metadata.Checksum))
            {
                // No checksum in version.xml — accept but log warning.
                // This is NOT a security boundary by itself; Authenticode
                // verification still provides independent trust.
                Logger.Warn("Update", "update.no_checksum_in_xml");
            }
            else
            {
                var actualChecksum = ComputeSha256Hex(tempFile);
                if (!string.Equals(actualChecksum, metadata.Checksum,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn("Update", "update.checksum_mismatch", new
                    {
                        expected = metadata.Checksum,
                        actual = actualChecksum
                    });
                    ShowFailure(progressWindow,
                        "文件校验失败：下载的文件与预期不符，可能已损坏或被篡改。\n\n" +
                        "请重新尝试更新。");
                    return;
                }
            }

            // ─── Phase 3: Authenticode verification ─────────────────

            if (RequireAuthenticodeSignature)
            {
                // ===== Strict enforcement mode (certificate purchased) =====

                progressWindow.ReportProgress(100, "正在验证数字签名...");

                // Primary verification: hardcoded ExpectedPublisher constant.
                var sigResult = AuthenticodeVerifier.Verify(tempFile, ExpectedPublisher);
                if (sigResult != AuthenticodeVerifier.Result.Valid)
                {
                    Logger.Warn("Update", "update.authenticode_failed", new
                    {
                        result = sigResult.ToString(),
                        expected_publisher = ExpectedPublisher,
                        publisher_from_xml = metadata.SignerSubject ?? "(none)"
                    });
                    ShowFailure(progressWindow,
                        $"数字签名验证失败：{AuthenticodeVerifier.GetResultDescription(sigResult)}\n\n" +
                        "为保障安全，已中止安装。请从 GitHub Releases 手动下载安装。");
                    return;
                }

                // Cross-check with version.xml signer info if present and different.
                if (!string.IsNullOrWhiteSpace(metadata.SignerSubject) &&
                    !string.Equals(metadata.SignerSubject, ExpectedPublisher, StringComparison.OrdinalIgnoreCase) &&
                    !metadata.SignerSubject.Contains(ExpectedPublisher, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn("Update", "update.signer_mismatch_xml_vs_code", new
                    {
                        code_publisher = ExpectedPublisher,
                        xml_publisher = metadata.SignerSubject
                    });
                    ShowFailure(progressWindow,
                        "签名发布者验证失败：安装包签名信息与当前版本不兼容。\n\n" +
                        "请从 GitHub Releases 手动更新到最新版本后再使用自动更新。");
                    return;
                }

                Logger.Info("Update", "update.authenticode_verified", new
                {
                    expected_publisher = ExpectedPublisher
                });
            }
            else
            {
                // ===== Advisory mode (no certificate yet) =====
                // SHA256 integrity check already completed above.
                // Authenticode is checked for diagnostic purposes only;
                // any result other than Valid is logged as a warning but
                // does NOT block the update.

                progressWindow.ReportProgress(100, "正在完成验证...");

                var sigResult = AuthenticodeVerifier.Verify(tempFile, ExpectedPublisher);
                if (sigResult == AuthenticodeVerifier.Result.Valid)
                {
                    Logger.Info("Update", "update.authenticode_advisory_ok", new
                    {
                        publisher = ExpectedPublisher
                    });
                }
                else
                {
                    Logger.Warn("Update", "update.authenticode_advisory_failed", new
                    {
                        result = sigResult.ToString(),
                        publisher = ExpectedPublisher,
                        note = "Warning only — Authenticode enforcement is off. " +
                               "Upgrade will proceed with SHA256 integrity check alone."
                    });
                }
            }

            // ─── Phase 4: Launch installer ──────────────────────────

            progressWindow.ReportProgress(100, "正在准备安装...");

            ProcessStartInfo psi;
            if (Environment.OSVersion.Version.Major >= 6)
            {
                // Windows Vista+: request administrator elevation
                psi = new ProcessStartInfo
                {
                    FileName = tempFile,
                    Arguments = installerArgs,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = tempFile,
                    Arguments = installerArgs,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };
            }

            Logger.Info("Update", "update.launching_installer",
                new { file = tempFile, args = installerArgs });

            progressWindow.ShowResult(true, "正在启动安装程序，请在弹出的用户账户控制(UAC)对话框中确认。");

            // Brief delay to let the user see the success message
            await Task.Delay(800, CancellationToken.None);

            Process.Start(psi);

            Logger.Info("Update", "update.installer_started");
            Logger.WriteShutdownTrace("update.installer_started",
                "Verified installer launched, shutting down app");

            // Exit the application gracefully
            OnApplicationExit();
        }
        catch (OperationCanceledException)
        {
            Logger.Info("Update", "update.cancelled");
            ShowFailure(progressWindow, "更新已被取消。");
        }
        catch (HttpRequestException ex)
        {
            Logger.Warn("Update", "update.download_failed",
                new { error_type = ex.GetType().Name });
            ShowFailure(progressWindow,
                $"下载失败：{ex.Message}\n\n请检查网络连接后重试。");
        }
        catch (Exception ex)
        {
            Logger.Warn("Update", "update.install_failed",
                new { error_type = ex.GetType().Name });
            ShowFailure(progressWindow,
                $"更新过程中发生错误：{ex.Message}\n\n请从 GitHub Releases 手动下载安装。");
        }
        finally
        {
            _installInProgress = false;

            // Clean up temp file in background (keep for a short while
            // in case the installer needs it, but it's in %TEMP% so
            // Windows will clean it eventually).
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5));
                try { File.Delete(tempFile); } catch { /* best effort */ }
            });
        }
    }

    /// <summary>
    /// Shows a failure message on the progress window.
    /// </summary>
    private static void ShowFailure(DownloadUpdateWindow window, string message)
    {
        try
        {
            if (window.Dispatcher.CheckAccess())
            {
                if (window.IsVisible)
                    window.ShowResult(false, message);
            }
            else
            {
                window.Dispatcher.Invoke(() =>
                {
                    if (window.IsVisible)
                        window.ShowResult(false, message);
                });
            }
        }
        catch
        {
            // Window may have been closed; fail silently
        }
    }

    /// <summary>
    /// Computes the uppercase hex SHA256 digest of a file.
    /// </summary>
    private static string ComputeSha256Hex(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Lightweight record for version.xml metadata needed during install.
    /// </summary>
    private sealed record VersionXmlMetadata(
        string? Checksum,
        string? SignerSubject);

    /// <summary>
    /// Fetches checksum and signer subject from version.xml.
    /// Returns a <see cref="VersionXmlMetadata"/> with available fields;
    /// missing/optional elements produce null values without failing.
    /// </summary>
    private static async Task<VersionXmlMetadata> FetchVersionMetadataAsync(CancellationToken ct)
    {
        try
        {
            using var response = await HttpClient.GetAsync(
                UpdateXmlUrl, HttpCompletionOption.ResponseContentRead, ct);

            if (!response.IsSuccessStatusCode)
                return new VersionXmlMetadata(null, null);

            var xml = await response.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);

            var checksumEl = doc.Root?.Element("checksum");
            var checksum = checksumEl?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(checksum))
                checksum = null;

            var signer = doc.Root?.Element("signer");
            var subject = signer?.Element("subject")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(subject))
                subject = null;

            if (subject is not null)
            {
                Logger.Info("Update", "update.signer_from_xml",
                    new { subject });
            }

            return new VersionXmlMetadata(checksum, subject);
        }
        catch (Exception ex)
        {
            Logger.Info("Update", "update.metadata_fetch_failed",
                new { reason = ex.GetType().Name });
            return new VersionXmlMetadata(null, null);
        }
    }

    /// <summary>
    /// Formats byte count for human-readable display.
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }

    // ─── Timeout & late-callback cleanup ──────────────────────────

    private static void StartTimeoutTimer()
    {
        StopTimeoutTimer();

        _timeoutTimer = new DispatcherTimer { Interval = CheckTimeout };
        _timeoutTimer.Tick += (_, _) =>
        {
            StopTimeoutTimer();

            var tcs = _pending;
            if (tcs is null) return;

            // 超时时不清空 _pending —— 阻止新检查在迟到回调到达前启动。
            // 先完成 TCS（调用方收到 Timeout），清理定时器负责释放 _pending。
            var gen = Interlocked.Read(ref _checkGeneration);
            Logger.Warn("Update", "update.check_timeout",
                new { gen, manual = _autoShowUpdateForm, timeout_s = CheckTimeout.TotalSeconds });

            tcs.TrySetResult(new UpdateCheckResult(UpdateCheckOutcome.Timeout, null));

            // 启动迟到回调宽限期：30 秒后若回调仍未到达，强制清空 _pending
            StartCleanupTimer();
        };
        _timeoutTimer.Start();
    }

    private static void StopTimeoutTimer()
    {
        _timeoutTimer?.Stop();
        _timeoutTimer = null;
    }

    /// <summary>
    /// 启动迟到回调清理定时器。宽限期到后强制清空 _pending，
    /// 允许下一次更新检查启动。
    /// </summary>
    private static void StartCleanupTimer()
    {
        StopCleanupTimer();

        _cleanupTimer = new DispatcherTimer { Interval = LateCallbackGracePeriod };
        _cleanupTimer.Tick += (_, _) =>
        {
            StopCleanupTimer();

            var tcs = _pending;
            if (tcs is null) return;

            _pending = null;
            Logger.Warn("Update", "update.late_callback_cleanup",
                new
                {
                    gen = Interlocked.Read(ref _checkGeneration),
                    grace_period_s = LateCallbackGracePeriod.TotalSeconds
                });
        };
        _cleanupTimer.Start();
    }

    private static void StopCleanupTimer()
    {
        _cleanupTimer?.Stop();
        _cleanupTimer = null;
    }

    // ─── Test hooks ────────────────────────────────────────────────

    /// <summary>
    /// [仅测试] 替换 AutoUpdater.NET 适配器为可控假实现。
    /// 调用后需重新执行 Configure() 以订阅假实现的事件。
    /// </summary>
    internal static void SetAdapterForTesting(IAutoUpdaterAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _configured = false;
    }

    /// <summary>
    /// [仅测试] 重置所有延后状态，恢复到初始空闲状态。
    /// </summary>
    internal static void ResetPendingState()
    {
        _pending = null;
        _lastAvailable = null;
        _installInProgress = false;
        _autoShowUpdateForm = false;
        _checkGeneration = 0;
        StopTimeoutTimer();
        StopCleanupTimer();
    }

    /// <summary>
    /// [仅测试] 模拟超时：完成当前 TCS 为 Timeout，保留 _pending 以阻止后续检查。
    /// </summary>
    internal static void SimulateTimeoutForTesting()
    {
        StopTimeoutTimer();
        var tcs = _pending;
        if (tcs is null) return;
        tcs.TrySetResult(new UpdateCheckResult(UpdateCheckOutcome.Timeout, null));
        // _pending is intentionally NOT cleared — this is the core isolation mechanism
        StartCleanupTimer();
    }

    /// <summary>
    /// [仅测试] 模拟迟到回调清理定时器到期：清空 _pending 允许下次检查。
    /// </summary>
    internal static void SimulateCleanupForTesting()
    {
        StopCleanupTimer();
        _pending = null;
    }

    /// <summary>
    /// 应用即将退出以执行更新安装。
    /// </summary>
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

// ─── Supporting types (unchanged from original) ──────────────────────

/// <summary>
/// AutoUpdater persistence adapter used when skip/remind-later behavior is disabled.
/// Returning no state also prevents legacy registry values from suppressing checks.
/// </summary>
internal sealed class NoOpUpdatePersistenceProvider : IPersistenceProvider
{
    public static NoOpUpdatePersistenceProvider Instance { get; } = new();

    private NoOpUpdatePersistenceProvider()
    {
    }

    public Version? GetSkippedVersion() => null;

    public DateTime? GetRemindLater() => null;

    public void SetSkippedVersion(Version? version)
    {
    }

    public void SetRemindLater(DateTime? remindLaterAt)
    {
    }
}

/// <summary>
/// Serializes a modal action and can defer it until the current callback returns.
/// </summary>
internal sealed class DeferredModalRunner
{
    private readonly Action<Action> _enqueue;
    private bool _scheduled;
    private bool _open;

    public DeferredModalRunner(Action<Action> enqueue)
    {
        _enqueue = enqueue;
    }

    public bool IsBusy => _scheduled || _open;

    public bool TryRun(Action action)
    {
        if (IsBusy) return false;

        _open = true;
        try
        {
            action();
            return true;
        }
        finally
        {
            _open = false;
        }
    }

    public bool TrySchedule(Action action)
    {
        if (IsBusy) return false;

        _scheduled = true;
        try
        {
            _enqueue(() =>
            {
                _scheduled = false;
                TryRun(action);
            });
        }
        catch
        {
            _scheduled = false;
            throw;
        }
        return true;
    }
}
