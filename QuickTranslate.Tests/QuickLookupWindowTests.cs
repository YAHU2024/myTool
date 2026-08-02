using System.Windows;
using System.Windows.Automation;
using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class QuickLookupWindowTests
{
    [Fact]
    public void Constructor_LoadsXamlAndExposesAccessibleCoreControls()
    {
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
                window.CloseForExit();
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

    private sealed class NoOpLookupService : IWordLookupService
    {
        public Task<WordLookupResult> LookupAsync(
            WordLookupRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoOpTtsService : ITtsService
    {
        public bool IsBusy => false;
        public event Action? StateChanged { add { } remove { } }
        public Task SpeakAsync(string text, string? languageHint, string? voiceOverride, double rate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
