using System;
using System.Threading;
using System.Windows;

namespace QuickTranslate.UI;

/// <summary>
/// Simple modal window that displays download progress and verification results
/// during the update installer download phase. Replaces AutoUpdater.NET's
/// built-in download dialog to enable Authenticode signature verification
/// before the installer is executed with administrator privileges.
/// </summary>
public partial class DownloadUpdateWindow : Window
{
    private CancellationTokenSource? _cts;

    public DownloadUpdateWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised when the user clicks Cancel during download.
    /// The downloader should observe the <see cref="CancellationToken"/> and abort.
    /// </summary>
    public event Action? Cancelled;

    /// <summary>
    /// CancellationToken that is signalled when the user cancels the download.
    /// </summary>
    public CancellationToken CancellationToken =>
        (_cts ??= new CancellationTokenSource()).Token;

    /// <summary>
    /// Updates the progress bar and status text during download.
    /// </summary>
    /// <param name="percentage">
    /// 0–100 for determinate progress, or -1 for indeterminate
    /// (unknown total size).
    /// </param>
    /// <param name="status">Human-readable status text.</param>
    public void ReportProgress(int percentage, string status)
    {
        if (!CheckAccess())
        {
            Dispatcher.Invoke(() => ReportProgress(percentage, status));
            return;
        }

        if (percentage < 0)
        {
            DownloadProgress.IsIndeterminate = true;
        }
        else
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = Math.Clamp(percentage, 0, 100);
        }
        DetailText.Text = status;
    }

    /// <summary>
    /// Transitions the window to show the final result (success or failure).
    /// </summary>
    /// <param name="success">Whether verification and installation preparation succeeded.</param>
    /// <param name="message">Result description to display.</param>
    public void ShowResult(bool success, string message)
    {
        if (!CheckAccess())
        {
            Dispatcher.Invoke(() => ShowResult(success, message));
            return;
        }

        if (success)
        {
            StatusText.Text = "准备安装";
            DownloadProgress.Value = 100;
        }
        else
        {
            StatusText.Text = "更新失败";
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
        }

        DetailText.Text = message;
        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        DetailText.Text = "正在取消...";
        _cts?.Cancel();
        Cancelled?.Invoke();
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Clean up cancellation token source when window is closing.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnClosed(e);
    }
}
