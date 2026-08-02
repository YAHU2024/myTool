using QuickTranslate.Core;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class TtsPlaybackCoordinatorTests
{
    [Fact]
    public async Task SpeakAsync_TracksOnlyCurrentOwnerAndIgnoresOldCompletion()
    {
        var service = new ControlledTtsService();
        using var coordinator = new TtsPlaybackCoordinator(service);

        var first = coordinator.SpeakAsync(
            TtsPlaybackOwner.QuickLookup, "one", null, null, 1, CancellationToken.None);
        await service.WaitForStartsAsync(1);
        var second = coordinator.SpeakAsync(
            TtsPlaybackOwner.FloatingResult, "two", null, null, 1, CancellationToken.None);
        await service.WaitForStartsAsync(2);

        Assert.False(coordinator.IsBusy(TtsPlaybackOwner.QuickLookup));
        Assert.True(coordinator.IsBusy(TtsPlaybackOwner.FloatingResult));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(coordinator.IsBusy(TtsPlaybackOwner.FloatingResult));

        service.CompleteCurrent();
        await second;
        Assert.False(coordinator.Current.IsBusy);
    }

    [Fact]
    public async Task StopAsync_ForOtherOwnerDoesNotStopCurrentPlayback()
    {
        var service = new ControlledTtsService();
        using var coordinator = new TtsPlaybackCoordinator(service);
        var playback = coordinator.SpeakAsync(
            TtsPlaybackOwner.QuickLookup, "one", null, null, 1, CancellationToken.None);
        await service.WaitForStartsAsync(1);

        await coordinator.StopAsync(TtsPlaybackOwner.FloatingResult);

        Assert.True(coordinator.IsBusy(TtsPlaybackOwner.QuickLookup));
        service.CompleteCurrent();
        await playback;
    }

    private sealed class ControlledTtsService : ITtsService
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource> _playbacks = new();
        private TaskCompletionSource _startChanged = NewSource();

        public bool IsBusy { get; private set; }
        public event Action? StateChanged;

        public Task SpeakAsync(
            string text,
            string? languageHint,
            string? voiceOverride,
            double rate,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource playback;
            lock (_sync)
            {
                playback = NewSource();
                _playbacks.Add(playback);
                IsBusy = true;
                _startChanged.TrySetResult();
                _startChanged = NewSource();
            }
            StateChanged?.Invoke();
            cancellationToken.Register(() => playback.TrySetCanceled(cancellationToken));
            return playback.Task;
        }

        public Task StopAsync()
        {
            TaskCompletionSource? active;
            lock (_sync)
            {
                active = _playbacks.LastOrDefault(item => !item.Task.IsCompleted);
                IsBusy = false;
            }
            active?.TrySetCanceled();
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public void CompleteCurrent()
        {
            lock (_sync)
            {
                _playbacks.Last(item => !item.Task.IsCompleted).TrySetResult();
                IsBusy = false;
            }
            StateChanged?.Invoke();
        }

        public async Task WaitForStartsAsync(int count)
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_playbacks.Count >= count)
                        return;
                    wait = _startChanged.Task;
                }
                await wait.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TaskCompletionSource NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
