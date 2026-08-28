using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using QuickTranslate.Helpers;
using QuickTranslate.Services;

namespace QuickTranslate.UI;

/// <summary>
/// Confirmation window shown when a new version is detected.
/// Displays version info and renders the latest release notes
/// (GitHub-rendered HTML from releases.atom) inside a WebView2 host.
/// </summary>
/// <remarks>
/// The window adapts to mandatory updates: when <see cref="Mandatory"/> is
/// <c>true</c>, the Remind Later button is hidden.
/// </remarks>
public partial class UpdateAvailableWindow : Window
{
    private readonly Uri? _changelogUri;
    private bool _ignoreNextCanceledNavigation;
    private bool _isClosed;

    /// <summary>
    /// True when the user clicked "Update now"; false when
    /// the user clicked Remind Later, opened GitHub, or closed the window.
    /// </summary>
    public bool UpdateConfirmed { get; private set; }

    /// <summary>
    /// Whether this update is mandatory (hides the Remind Later button).
    /// </summary>
    public bool Mandatory
    {
        get => !RemindLaterButton.IsVisible;
        set => RemindLaterButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
    }

    public UpdateAvailableWindow(string? currentVersion, string? newVersion, string? changelogUrl)
    {
        InitializeComponent();

        CurrentVersionText.Text = currentVersion ?? "未知";
        NewVersionText.Text = newVersion ?? "未知";
        TryGetSafeChangelogUri(changelogUrl, out _changelogUri);
        Loaded += UpdateAvailableWindow_Loaded;
    }

    internal static bool TryGetSafeChangelogUri(string? value, out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            candidate.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(candidate.UserInfo) &&
            !string.IsNullOrWhiteSpace(candidate.Host))
        {
            uri = candidate;
            return true;
        }

        uri = null;
        return false;
    }

    private async void UpdateAvailableWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= UpdateAvailableWindow_Loaded;

        if (_changelogUri is null)
        {
            ShowChangelogError("无法显示更新说明", "更新说明地址无效。", canOpenExternally: false);
            Logger.Warn("Update", "update.changelog_invalid_url", new { });
            return;
        }

        try
        {
            var contentHtml = await UpdateService.FetchReleaseNotesHtmlAsync(_changelogUri.AbsoluteUri);
            if (_isClosed)
                return;

            if (string.IsNullOrWhiteSpace(contentHtml))
            {
                ShowChangelogError(
                    "无法获取更新说明",
                    "请检查网络连接，可直接点击「立即更新」开始安装，或在浏览器中查看。",
                    canOpenExternally: true);
                return;
            }

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickTranslate",
                "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);

            if (_isClosed)
                return;

            await ChangelogBrowser.EnsureCoreWebView2Async(environment);
            if (_isClosed)
                return;

            ConfigureBrowser();
            ChangelogBrowser.NavigateToString(BuildStyledReleaseHtml(contentHtml));
        }
        catch (Exception) when (_isClosed)
        {
            // Closing the window can interrupt WebView2 initialization.
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or COMException)
        {
            Logger.Warn("Update", "update.changelog_browser_unavailable", new { error_type = ex.GetType().Name });
            ShowChangelogError(
                "无法加载更新说明",
                "WebView2 不可用，可以改用系统浏览器查看。",
                canOpenExternally: true);
        }
        catch (Exception ex)
        {
            Logger.Warn("Update", "update.changelog_browser_failed", new { error_type = ex.GetType().Name });
            ShowChangelogError(
                "无法显示更新说明",
                "可以改用系统浏览器查看。",
                canOpenExternally: true);
        }
    }

    /// <summary>
    /// 将 GitHub 渲染的 release 说明 HTML 片段包装为完整文档，并注入与窗口一致的暗色主题样式。
    /// </summary>
    private static string BuildStyledReleaseHtml(string contentHtml)
    {
        const string Css = @"
body { background:#16191F; color:#E8E8E8; font-family:'Microsoft YaHei UI','Segoe UI',sans-serif; margin:14px; font-size:13px; line-height:1.7; }
h1,h2,h3,h4 { color:#F2F4F7; line-height:1.4; }
h1 { font-size:18px; } h2 { font-size:16px; border-bottom:1px solid #343D49; padding-bottom:4px; } h3 { font-size:14px; }
a { color:#66A9FF; }
code { background:#242424; border-radius:3px; padding:1px 4px; font-family:Consolas,'Courier New',monospace; font-size:12px; }
pre { background:#242424; border-radius:5px; padding:10px; overflow-x:auto; }
pre code { background:transparent; padding:0; }
table { border-collapse:collapse; margin:8px 0; }
th,td { border:1px solid #555; padding:6px 10px; text-align:left; }
th { background:#2D2D30; }
blockquote { border-left:3px solid #555; margin:8px 0; padding:4px 12px; color:#B8B8B8; background:#2D2D30; }
hr { border:none; border-top:1px solid #555; }
img { max-width:100%; border-radius:4px; }
input[type=checkbox] { accent-color:#4DB6AC; }
li { margin:2px 0; }
::-webkit-scrollbar { width:8px; height:8px; }
::-webkit-scrollbar-thumb { background:#647180; border-radius:3px; }
::-webkit-scrollbar-thumb:hover { background:#8995A3; }
::-webkit-scrollbar-thumb:active { background:#4DB6AC; }
::-webkit-scrollbar-track { background:transparent; }
";
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"/><style>" + Css
            + "</style></head><body>" + contentHtml + "</body></html>";
    }

    private void ConfigureBrowser()
    {
        var settings = ChangelogBrowser.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        ChangelogBrowser.CoreWebView2.NewWindowRequested += ChangelogBrowser_NewWindowRequested;
    }

    private void ChangelogBrowser_NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        // NavigateToString 的内存页导航直接放行（e.Uri 为空 / about: / data:）
        if (string.IsNullOrEmpty(e.Uri) ||
            e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TryGetSafeChangelogUri(e.Uri, out _))
        {
            _ignoreNextCanceledNavigation = true;
            e.Cancel = true;
            Logger.Warn("Update", "update.changelog_navigation_rejected", new { });
            ShowChangelogError(
                "更新说明地址不安全",
                "页面尝试跳转到非 HTTPS 地址，已阻止加载。",
                canOpenExternally: true);
            return;
        }

        if (!e.IsUserInitiated)
            return;

        _ignoreNextCanceledNavigation = true;
        e.Cancel = true;
        OpenExternalUri(e.Uri);
    }

    private void ChangelogBrowser_NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.IsUserInitiated)
            OpenExternalUri(e.Uri);
    }

    private void ChangelogBrowser_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled &&
            _ignoreNextCanceledNavigation)
        {
            _ignoreNextCanceledNavigation = false;
            return;
        }

        _ignoreNextCanceledNavigation = false;

        if (_isClosed || e.IsSuccess)
        {
            if (!_isClosed)
            {
                ChangelogStatusPanel.Visibility = Visibility.Collapsed;
                ChangelogBrowser.Visibility = Visibility.Visible;
            }
            return;
        }

        Logger.Warn("Update", "update.changelog_navigation_failed", new
        {
            web_error_status = e.WebErrorStatus.ToString(),
            http_status = e.HttpStatusCode
        });
        ShowChangelogError(
            "更新说明加载失败",
            "请检查网络连接，或改用系统浏览器查看。",
            canOpenExternally: true);
    }

    private void ShowChangelogError(string title, string detail, bool canOpenExternally)
    {
        if (_isClosed)
            return;

        ChangelogBrowser.Visibility = Visibility.Collapsed;
        ChangelogStatusPanel.Visibility = Visibility.Visible;
        ChangelogLoadingBar.Visibility = Visibility.Collapsed;
        ChangelogStatusTitle.Text = title;
        ChangelogStatusDetail.Text = detail;
        OpenInBrowserButton.Visibility = canOpenExternally && _changelogUri is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (_changelogUri is not null)
            OpenExternalUri(_changelogUri.AbsoluteUri);
    }

    private void OpenExternalUri(string value)
    {
        if (!TryGetSafeChangelogUri(value, out var uri) || uri is null)
        {
            Logger.Warn("Update", "update.changelog_external_url_rejected", new { });
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn("Update", "update.changelog_external_open_failed", new { error_type = ex.GetType().Name });
            ShowChangelogError(
                "无法打开系统浏览器",
                "请稍后重试。",
                canOpenExternally: true);
        }
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateConfirmed = true;
        Close();
    }

    private void RemindLaterButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        if (_changelogUri is not null)
            OpenExternalUri(_changelogUri.AbsoluteUri);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        if (ChangelogBrowser.CoreWebView2 is not null)
            ChangelogBrowser.CoreWebView2.NewWindowRequested -= ChangelogBrowser_NewWindowRequested;
        ChangelogBrowser.Dispose();

        base.OnClosed(e);
    }
}
