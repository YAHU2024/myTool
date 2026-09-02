using System.Windows.Threading;
using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ScreenshotTranslationOverlayWindowTests
{
    private static bool IsRunningOnCI =>
        Environment.GetEnvironmentVariable("CI") == "true" ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

    [SkippableFact]
    public void IncrementalUpdate_UsesRealWpfMeasurementBeforeShowingLongTranslation()
    {
        Skip.If(IsRunningOnCI, "WPF window tests require a real message pump, unavailable on headless CI.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            ScreenshotTranslationOverlayWindow? window = null;
            try
            {
                var unit = new ScreenshotTranslationUnit(
                    "u0001",
                    "OK",
                    Array.Empty<OcrTextBlock>(),
                    new OcrBounds(20, 20, 28, 20));
                window = new ScreenshotTranslationOverlayWindow(
                    new ScreenshotRegion(0, 0, 400, 300),
                    new OcrImage(400, 300, 1_600, new byte[480_000]),
                    new[] { unit });

                var accepted = window.TryUpdateTranslation(new TranslatedTextUnit(
                    "u0001",
                    "这是明显长于 OCR 原文的最终译文，必须经过真实 WPF 测量后才能显示完整内容"));

                Assert.True(accepted);
                Assert.Equal(1, window.CompletedCount);
                var layout = Assert.Single(window.LayoutResult.Items);
                Assert.NotEqual(ScreenshotOverlayLayoutStatus.Skipped, layout.Status);
                Assert.True(layout.IsTextFullyContained);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window?.Close();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
    }
}
