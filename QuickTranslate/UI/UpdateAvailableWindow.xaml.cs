using System;
using System.Windows;
using System.Windows.Forms.Integration;

namespace QuickTranslate.UI;

/// <summary>
/// Confirmation window shown when a new version is detected.
/// Displays version info and renders the changelog URL in an embedded
/// <see cref="System.Windows.Forms.WebBrowser"/> — matching the behaviour
/// of AutoUpdater.NET's built-in <c>ShowUpdateForm</c>.
/// </summary>
/// <remarks>
/// The window adapts to mandatory updates: when <see cref="Mandatory"/> is
/// <c>true</c>, the Skip and Remind Later buttons are hidden, leaving only
/// the Update button.
/// </remarks>
public partial class UpdateAvailableWindow : Window
{
    private readonly System.Windows.Forms.WebBrowser _browser;

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

        // Create WebBrowser in code-behind so we can control
        // script errors and navigation behaviour.
        _browser = new System.Windows.Forms.WebBrowser
        {
            ScriptErrorsSuppressed = true,
            AllowNavigation = true,
            AllowWebBrowserDrop = false,
            IsWebBrowserContextMenuEnabled = false,
            WebBrowserShortcutsEnabled = false,
            ScrollBarsEnabled = true
        };

        BrowserHost.Child = _browser;

        Loaded += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(changelogUrl))
                _browser.Navigate(changelogUrl);
        };
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
    /// Clean up WebBrowser resources when the window is closing.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        if (_browser is not null)
        {
            _browser.Stop();
            _browser.Dispose();
        }

        base.OnClosed(e);
    }
}
