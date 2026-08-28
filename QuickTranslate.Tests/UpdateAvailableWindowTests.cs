using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class UpdateAvailableWindowTests
{
    [Theory]
    [InlineData("https://github.com/YAHU2024/myTool/releases/tag/v1.9.0")]
    [InlineData("https://example.test/releases/latest?source=update")]
    public void TryGetSafeChangelogUri_AcceptsAbsoluteHttps(string value)
    {
        var accepted = UpdateAvailableWindow.TryGetSafeChangelogUri(value, out var uri);

        Assert.True(accepted);
        Assert.NotNull(uri);
        Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("release-notes.html")]
    [InlineData("http://example.test/releases/latest")]
    [InlineData("file:///C:/release-notes.html")]
    [InlineData("javascript:alert('update')")]
    [InlineData("https://user:password@example.test/releases/latest")]
    public void TryGetSafeChangelogUri_RejectsUnsafeOrInvalidValues(string? value)
    {
        var accepted = UpdateAvailableWindow.TryGetSafeChangelogUri(value, out var uri);

        Assert.False(accepted);
        Assert.Null(uri);
    }

    [Fact]
    public void Constructor_ShowsLoadingStateBeforeChangelogRendered()
    {
        RunOnSta(() =>
        {
            var window = new UpdateAvailableWindow(
                "1.8.7",
                "1.9.0",
                "https://github.com/YAHU2024/myTool/releases/tag/v1.9.0");

            try
            {
                Assert.Equal("1.8.7", window.CurrentVersionText.Text);
                Assert.Equal("1.9.0", window.NewVersionText.Text);
                Assert.Equal(Visibility.Collapsed, window.ChangelogBox.Visibility);
                Assert.Equal(Visibility.Visible, window.ChangelogStatusPanel.Visibility);
                Assert.Equal(Visibility.Visible, window.ChangelogLoadingBar.Visibility);
                Assert.Equal(Visibility.Collapsed, window.OpenInBrowserButton.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Mandatory_HidesDeferralActions()
    {
        RunOnSta(() =>
        {
            var window = new UpdateAvailableWindow("1.8.7", "1.9.0", null)
            {
                Mandatory = true
            };

            try
            {
                Assert.Equal(Visibility.Collapsed, window.SkipButton.Visibility);
                Assert.Equal(Visibility.Collapsed, window.RemindLaterButton.Visibility);
                Assert.Equal(Visibility.Visible, window.UpdateButton.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void RunOnSta(Action assertion)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                assertion();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw failure;
    }
}
