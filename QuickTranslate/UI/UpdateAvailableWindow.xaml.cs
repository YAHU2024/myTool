using System;
using System.Diagnostics;
using System.Windows;
using QuickTranslate.Helpers;
using QuickTranslate.Services;

namespace QuickTranslate.UI;

/// <summary>
/// Confirmation window shown when a new version is detected.
/// Displays version info and renders the latest release notes (Markdown) locally.
/// </summary>
/// <remarks>
/// The window adapts to mandatory updates: when <see cref="Mandatory"/> is
/// <c>true</c>, the Skip and Remind Later buttons are hidden, leaving only
/// the Update button.
/// </remarks>
public partial class UpdateAvailableWindow : Window
{
    private readonly Uri? _changelogUri;
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

        MarkdownInteraction.ConfigureSelectableHost(ChangelogBox, "更新说明");

        try
        {
            var markdown = await UpdateService.FetchReleaseNotesMarkdownAsync(
                _changelogUri.AbsoluteUri);
            if (_isClosed)
                return;

            if (string.IsNullOrWhiteSpace(markdown))
            {
                ShowChangelogError(
                    "无法获取更新说明",
                    "请检查网络连接，可直接点击「立即更新」开始安装，或在浏览器中查看。",
                    canOpenExternally: true);
                return;
            }

            var result = MarkdownRenderer.RenderDetailed(markdown);
            ChangelogBox.Document = result.Document;
            ChangelogStatusPanel.Visibility = Visibility.Collapsed;
            ChangelogBox.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (_isClosed)
                return;

            Logger.Warn("Update", "update.changelog_render_failed", new { error_type = ex.GetType().Name });
            ShowChangelogError(
                "无法显示更新说明",
                "可以改用系统浏览器查看。",
                canOpenExternally: true);
        }
    }

    private void ShowChangelogError(string title, string detail, bool canOpenExternally)
    {
        if (_isClosed)
            return;

        ChangelogBox.Visibility = Visibility.Collapsed;
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

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        base.OnClosed(e);
    }
}
