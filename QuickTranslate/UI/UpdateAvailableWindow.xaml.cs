using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using QuickTranslate.Helpers;

namespace QuickTranslate.UI;

/// <summary>
/// Confirmation window shown when a new version is detected.
/// Displays version info and renders the changelog URL in WebView2.
/// </summary>
/// <remarks>
/// The window adapts to mandatory updates: when <see cref="Mandatory"/> is
/// <c>true</c>, the Skip and Remind Later buttons are hidden, leaving only
/// the Update button.
/// </remarks>
public partial class UpdateAvailableWindow : Window
{
    private readonly Uri? _changelogUri;
    private bool _ignoreNextCanceledNavigation;
    private bool _isClosed;

    /// <summary>
    /// True when the user clicked "Update now"; false when
    /// the user clicked Skip, Remind Later, or closed the window.
    /// </summary>
    public bool UpdateConfirmed { get; private set; }

    /// <summary>
    /// Whether this update is mandatory (hides Skip/Remind Later buttons).
    /// </summary>
    public bool Mandatory
    {
        get => !SkipButton.IsVisible;
        set
        {
            var visible = !value;
            SkipButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            RemindLaterButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
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
            ChangelogBrowser.Source = _changelogUri;
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
                "无法加载更新说明",
                "内嵌页面初始化失败，可以改用系统浏览器查看。",
                canOpenExternally: true);
        }
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

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Clean up WebView2 resources when the window is closing.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        if (ChangelogBrowser.CoreWebView2 is not null)
            ChangelogBrowser.CoreWebView2.NewWindowRequested -= ChangelogBrowser_NewWindowRequested;
        ChangelogBrowser.Dispose();

        base.OnClosed(e);
    }
}
