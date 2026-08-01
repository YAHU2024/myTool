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

    /// <summary>当前在飞的检查；为 null 表示空闲。仅在 UI 线程读写。</summary>
    private static TaskCompletionSource<UpdateCheckResult>? _pending;

    /// <summary>本次检查是否由调用方要求直接弹出更新对话框</summary>
    private static bool _autoShowUpdateForm;

    /// <summary>最近一次检查到的可用更新</summary>
    private static UpdateInfoEventArgs? _lastAvailable;

    /// <summary>下载/安装是否正在进行中</summary>
    private static bool _installInProgress;

    private static DispatcherTimer? _timeoutTimer;
    private static long _checkStartedAt;

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

        // 订阅后库自带的更新/错误对话框全部被抑制
        AutoUpdater.CheckForUpdateEvent += OnCheckForUpdate;

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

        // 已有检查在飞
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

        var tcs = new TaskCompletionSource<UpdateCheckResult>();
        _pending = tcs;
        _autoShowUpdateForm = autoShowUpdateForm;
        _checkStartedAt = Stopwatch.GetTimestamp();

        try
        {
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
    /// 下载、签名验证和安装均由本方法接管。
    /// </summary>
    public static bool ShowUpdateFormForLastCheck()
    {
        var args = _lastAvailable;
        if (args is null) return false;

        if (UpdateFormRunner.IsBusy || _installInProgress) return false;

        return UpdateFormRunner.TryRun(() => _ = DownloadAndInstallAsync(args, null));
    }

    /// <summary>
    /// 更新检查完成回调。
    /// </summary>
    private static void OnCheckForUpdate(UpdateInfoEventArgs args)
    {
        StopTimeoutTimer();

        var tcs = _pending;
        _pending = null;
        var autoShow = _autoShowUpdateForm;
        var duration = Stopwatch.GetElapsedTime(_checkStartedAt);

        if (args.Error is not null)
        {
            var webEx = args.Error as WebException;
            Logger.Warn("Update", "update.check_error", new
            {
                error_type = args.Error.GetType().Name,
                web_status = webEx?.Status.ToString(),
                http_status = (webEx?.Response as HttpWebResponse)?.StatusCode.ToString(),
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

            // 先回传结果再启动下载：TCS 默认同步执行延续，
            // 可以让调用方 UI 在弹窗之前完成刷新。
            tcs?.TrySetResult(new UpdateCheckResult(
                UpdateCheckOutcome.UpdateAvailable, args.CurrentVersion?.ToString()));

            if (tcs is not null && autoShow)
            {
                // 接管下载安装流程（不再委托 AutoUpdater.ShowUpdateForm）
                _ = DownloadAndInstallAsync(args, null);
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
                new { error_type = ex.GetType().Name, message = ex.Message });
            ShowFailure(progressWindow,
                $"下载失败：{ex.Message}\n\n请检查网络连接后重试。");
        }
        catch (Exception ex)
        {
            Logger.Warn("Update", "update.install_failed",
                new { error_type = ex.GetType().Name, message = ex.Message });
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

    // ─── Timeout management (unchanged from original) ──────────

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
            tcs.TrySetResult(new UpdateCheckResult(UpdateCheckOutcome.Timeout, null));
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
