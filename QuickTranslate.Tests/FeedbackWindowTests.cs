using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class FeedbackWindowTests
{
    private static bool IsRunningOnCI =>
        Environment.GetEnvironmentVariable("CI") == "true" ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

    [SkippableFact]
    public void Constructor_LoadsSharedSettingsStyles()
    {
        Skip.If(IsRunningOnCI, "WPF window tests require a real message pump, unavailable on headless CI.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new FeedbackWindow();
                Assert.NotNull(window.FindResource("SecondaryButton"));
                Assert.NotNull(window.FindResource("PrimaryButton"));
                Assert.NotNull(window.FindResource("DarkComboBox"));
                Assert.NotNull(window.FindResource(typeof(ScrollBar)));
                Assert.Equal(48, window.ClearDiagnosticsButton.Width);
                Assert.Equal(HorizontalAlignment.Right, window.ClearDiagnosticsButton.HorizontalAlignment);
                var firstField = Assert.IsType<DockPanel>(window.FieldsPanel.Children[0]);
                var copyButton = Assert.IsType<Button>(firstField.Children[0]);
                Assert.Same(window.FindResource("CompactButton"), copyButton.Style);
                window.Close();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
    }
}
