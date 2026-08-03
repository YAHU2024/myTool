using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class QuickLookupWindowTests
{
    private static bool IsRunningOnCI =>
        Environment.GetEnvironmentVariable("CI") == "true" ||
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

    [Fact]
    public void Constructor_LoadsXamlAndExposesAccessibleCoreControls()
    {
        Skip.If(IsRunningOnCI, "WPF window tests require a real message pump, unavailable on headless CI.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var ttsService = new NoOpTtsService();
                using var playback = new TtsPlaybackCoordinator(ttsService);
                using var sessions = new WordLookupSessionCoordinator();
                var window = new QuickLookupWindow(
                    new NoOpLookupService(),
                    new NoOpEnrichmentService(),
                    sessions,
                    new RecentLookupBuffer(),
                    playback,
                    "简体中文");

                Assert.Equal(420, window.Width);
                Assert.Equal(600, window.Height);
                Assert.Equal("查词输入框", AutomationProperties.GetName(window.QueryTextBox));
                Assert.Equal("查询", AutomationProperties.GetName(window.SubmitButton));
                Assert.Equal("朗读词头", AutomationProperties.GetName(window.SpeakHeadwordButton));
                Assert.Equal("复制查词结果", AutomationProperties.GetName(window.CopyButton));
                Assert.Equal("AI 补全中文", AutomationProperties.GetName(window.EnrichButton));
                Assert.Same(window.FindResource("EnrichmentButton"), window.EnrichButton.Style);
                Assert.Same(window.FindResource("Win11ScrollViewer"), window.ResultScroller.Style);
                var scope = sessions.Begin("run");
                Assert.True(sessions.TryComplete(scope, LocalResultWithMissingChinese()));
                Assert.Equal(Visibility.Visible, window.EnrichButton.Visibility);
                window.CloseForExit();
                PumpDispatcher();
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

    [Fact]
    public void EnrichmentButton_ShowsBusyStateImmediately()
    {
        Skip.If(IsRunningOnCI, "WPF window tests require a real message pump, unavailable on headless CI.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var ttsService = new NoOpTtsService();
                using var playback = new TtsPlaybackCoordinator(ttsService);
                using var sessions = new WordLookupSessionCoordinator();
                var window = new QuickLookupWindow(
                    new NoOpLookupService(),
                    new PendingEnrichmentService(),
                    sessions,
                    new RecentLookupBuffer(),
                    playback,
                    "简体中文");
                var scope = sessions.Begin("run");
                Assert.True(sessions.TryComplete(scope, LocalResultWithMissingChinese()));

                window.EnrichButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

                Assert.False(window.EnrichButton.IsEnabled);
                Assert.Equal("AI 补全中...", window.EnrichButtonLabel.Text);
                Assert.Equal("AI 中文补全中", AutomationProperties.GetName(window.EnrichButton));
                Assert.Equal(Visibility.Collapsed, window.EnrichButtonIcon.Visibility);
                Assert.Equal(Visibility.Visible, window.EnrichProgress.Visibility);
                window.CloseForExit();
                PumpDispatcher();
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

    /// <summary>
    /// Drains pending WPF cleanup operations queued by <see cref="Window.Close"/>.
    /// Required on headless CI where the Dispatcher lacks a native message pump.
    /// </summary>
    private static void PumpDispatcher()
    {
        Dispatcher.CurrentDispatcher.Invoke(
            () => { },
            DispatcherPriority.Background);
    }

    private sealed class NoOpLookupService : IWordLookupService
    {
        public Task<WordLookupResult> LookupAsync(
            WordLookupRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoOpEnrichmentService : IWordLookupEnrichmentService
    {
        public Task<WordLookupResult> EnrichAsync(
            WordLookupRequest request,
            WordLookupResult localResult,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class PendingEnrichmentService : IWordLookupEnrichmentService
    {
        public async Task<WordLookupResult> EnrichAsync(
            WordLookupRequest request,
            WordLookupResult localResult,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return localResult;
        }
    }

    private sealed class NoOpTtsService : ITtsService
    {
        public bool IsBusy => false;
        public event Action? StateChanged { add { } remove { } }
        public Task SpeakAsync(string text, string? languageHint, string? voiceOverride, double rate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static WordLookupResult LocalResultWithMissingChinese() => new(
        "run",
        Array.Empty<WordPronunciation>(),
        [new WordSense("动词", "跑", "move quickly")],
        [new WordExample("They run daily.", string.Empty)],
        Array.Empty<string>(),
        new WordLookupSource(
            "ecdict-oewn-local",
            "本地词典 · ECDICT + OEWN",
            WordLookupSourceKind.Dictionary));
}
