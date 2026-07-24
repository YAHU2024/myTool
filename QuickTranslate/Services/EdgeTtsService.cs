using System.IO;
using System.Net.WebSockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using QuickTranslate.Helpers;

namespace QuickTranslate.Services;

/// <summary>
/// Orchestrates Edge synthesis, temp-file playback, cancellation, and latest-speak-wins.
/// </summary>
public sealed class EdgeTtsService : ITtsService
{
    internal const int MaxAttemptsPerVoice = 2;
    private const int MediaOpenTimeoutMs = 5_000;
    private const int RetryDelayMinMs = 200;
    private const int RetryDelayMaxMs = 400;

    private readonly EdgeTtsClient _client;
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private CancellationTokenSource? _speakCts;
    private MediaPlayer? _player;
    private string? _currentTempFile;
    private long _speakId;
    private bool _isBusy;
    private bool _disposed;
    private string? _lastUiHint;

    public EdgeTtsService(EdgeTtsClient? client = null, Dispatcher? dispatcher = null)
    {
        _client = client ?? new EdgeTtsClient();
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public bool IsBusy
    {
        get { lock (_gate) return _isBusy; }
    }

    public event Action? StateChanged;

    /// <summary>
    /// One-shot short UI tip from the last speak (no spoken text). Null if none.
    /// </summary>
    public string? TakeLastUiHint()
    {
        lock (_gate)
        {
            var hint = _lastUiHint;
            _lastUiHint = null;
            return hint;
        }
    }

    public async Task SpeakAsync(
        string text,
        string? languageHint,
        string? voiceOverride,
        double rate,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var speakId = Interlocked.Increment(ref _speakId);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _speakCts;
            _speakCts = linked;
            _isBusy = true;
            _lastUiHint = null;
        }

        if (previous is not null)
        {
            try { previous.Cancel(); }
            catch { /* ignore */ }
            previous.Dispose();
        }

        RaiseStateChanged();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? tempPath = null;
        Exception? failure = null;
        var cancelled = false;
        var plan = TtsTextSelector.CreateSpeakPlan(text, voiceOverride, rate, maxChars: 0);
        // Prefer caller's language hint only when it is non-empty; plan always has a value.
        if (!string.IsNullOrWhiteSpace(languageHint))
        {
            plan = plan with { LanguageHint = languageHint.Trim() };
        }

        try
        {
            await StopPlaybackCoreAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(plan.Text))
                throw new TtsSpeakException(
                    TtsSpeakException.Protocol,
                    plan.SelectionMode,
                    plan.Voice,
                    attempt: 0,
                    "No speakable text.");

            Logger.Info("Tts", "tts.speak.started", new Dictionary<string, object?>
            {
                ["text_len"] = plan.Text.Length,
                ["voice"] = plan.Voice,
                ["rate"] = plan.Rate,
                ["speak_id"] = speakId,
                ["language_hint"] = plan.LanguageHint,
                ["selection_mode"] = plan.SelectionMode,
                ["voice_source"] = plan.VoiceSource
            });

            var (audio, attemptUsed, finalPlan) = await SynthesizeWithPolicyAsync(plan, linked.Token)
                .ConfigureAwait(false);

            tempPath = CreateTempAudioPath();
            await File.WriteAllBytesAsync(tempPath, audio, linked.Token).ConfigureAwait(false);

            lock (_gate)
            {
                if (!ReferenceEquals(_speakCts, linked))
                    return;
                _currentTempFile = tempPath;
            }

            await PlayFileAsync(tempPath, finalPlan.SelectionMode, finalPlan.Voice, linked.Token).ConfigureAwait(false);

            if (IsCurrentSpeak(speakId, linked))
            {
                if (string.Equals(finalPlan.VoiceSource, TtsTextSelector.VoiceSourceFallback, StringComparison.Ordinal))
                {
                    lock (_gate)
                        _lastUiHint = "已改用中文音色";
                }

                Logger.Info("Tts", "tts.speak.completed", new Dictionary<string, object?>
                {
                    ["duration_ms"] = sw.ElapsedMilliseconds,
                    ["speak_id"] = speakId,
                    ["audio_bytes"] = audio.Length,
                    ["attempt"] = attemptUsed,
                    ["voice"] = finalPlan.Voice,
                    ["voice_source"] = finalPlan.VoiceSource,
                    ["selection_mode"] = finalPlan.SelectionMode
                });
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            if (IsCurrentSpeak(speakId, linked))
            {
                Logger.Info("Tts", "tts.speak.cancelled", new Dictionary<string, object?>
                {
                    ["speak_id"] = speakId,
                    ["error_kind"] = TtsSpeakException.Cancelled,
                    ["selection_mode"] = plan.SelectionMode,
                    ["voice"] = plan.Voice
                });
            }
        }
        catch (Exception ex)
        {
            failure = ex is TtsSpeakException
                ? ex
                : new TtsSpeakException(
                    TtsSpeakException.Classify(ex, linked.Token),
                    plan.SelectionMode,
                    plan.Voice,
                    attempt: 0,
                    ex.Message,
                    ex);

            if (IsCurrentSpeak(speakId, linked))
            {
                var kind = failure is TtsSpeakException tse
                    ? tse.ErrorKind
                    : TtsSpeakException.Classify(failure, linked.Token);
                var attempt = failure is TtsSpeakException tse2 ? tse2.Attempt : 0;
                var voice = failure is TtsSpeakException tse3 ? tse3.Voice : plan.Voice;
                Logger.Error("Tts", "tts.speak.failed", new Dictionary<string, object?>
                {
                    ["speak_id"] = speakId,
                    ["exception_type"] = failure.GetType().Name,
                    ["error_kind"] = kind,
                    ["voice"] = voice,
                    ["text_len"] = plan.Text.Length,
                    ["attempt"] = attempt,
                    ["selection_mode"] = plan.SelectionMode
                }, failure);
            }
        }
        finally
        {
            if (IsCurrentSpeak(speakId, linked))
            {
                await StopPlaybackCoreAsync().ConfigureAwait(false);
                DeleteTempFile(_currentTempFile);
                DeleteTempFile(tempPath);
                _currentTempFile = null;
                lock (_gate)
                {
                    if (ReferenceEquals(_speakCts, linked))
                        _speakCts = null;
                    _isBusy = false;
                }

                linked.Dispose();
                RaiseStateChanged();
            }
            else
            {
                DeleteTempFile(tempPath);
                linked.Dispose();
            }
        }

        if (failure is not null && !cancelled)
            throw failure;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _speakCts;
            _speakCts = null;
            _isBusy = false;
        }

        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch { /* ignore */ }
            cts.Dispose();
        }

        await StopPlaybackCoreAsync().ConfigureAwait(false);
        DeleteTempFile(_currentTempFile);
        _currentTempFile = null;
        RaiseStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        await RunOnDispatcherAsync(() =>
        {
            if (_player is null)
                return;
            try { _player.Close(); }
            catch { /* ignore */ }
            _player = null;
        }).ConfigureAwait(false);
    }

    private async Task<(byte[] Audio, int AttemptUsed, TtsTextSelector.SpeakPlan FinalPlan)> SynthesizeWithPolicyAsync(
        TtsTextSelector.SpeakPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            var (audio, attempt) = await SynthesizeWithRetriesAsync(plan, cancellationToken).ConfigureAwait(false);
            return (audio, attempt, plan);
        }
        catch (TtsSpeakException ex)
            when (TtsSpeakException.ShouldFallbackToXiaoxiao(
                plan.SelectionMode,
                plan.LanguageHint,
                plan.Voice,
                ex.ErrorKind))
        {
            var fallback = TtsTextSelector.WithFallbackVoice(plan, TtsTextSelector.VoiceXiaoxiao);
            Logger.Info("Tts", "tts.speak.voice_fallback", new Dictionary<string, object?>
            {
                ["from"] = plan.Voice,
                ["to"] = fallback.Voice,
                ["lang"] = plan.LanguageHint,
                ["reason"] = TtsSpeakException.EmptyAudio,
                ["selection_mode"] = plan.SelectionMode
            });

            var (audio, attempt) = await SynthesizeWithRetriesAsync(fallback, cancellationToken).ConfigureAwait(false);
            return (audio, attempt, fallback);
        }
    }

    private async Task<(byte[] Audio, int AttemptUsed)> SynthesizeWithRetriesAsync(
        TtsTextSelector.SpeakPlan plan,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        string lastKind = TtsSpeakException.Protocol;

        for (var attempt = 1; attempt <= MaxAttemptsPerVoice; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var audio = await _client.SynthesizeAsync(plan.Text, plan.Voice, plan.Rate, cancellationToken)
                    .ConfigureAwait(false);
                if (audio.Length == 0)
                    throw new InvalidOperationException("Empty audio payload.");
                return (audio, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                lastKind = TtsSpeakException.Classify(ex, cancellationToken);
                if (lastKind == TtsSpeakException.Cancelled || cancellationToken.IsCancellationRequested)
                    throw;

                var retryable = TtsSpeakException.IsRetryable(lastKind) && attempt < MaxAttemptsPerVoice;
                if (retryable)
                {
                    Logger.Info("Tts", "tts.speak.retry", new Dictionary<string, object?>
                    {
                        ["attempt"] = attempt,
                        ["error_kind"] = lastKind,
                        ["voice"] = plan.Voice,
                        ["selection_mode"] = plan.SelectionMode,
                        ["text_len"] = plan.Text.Length
                    });

                    var delayMs = Random.Shared.Next(RetryDelayMinMs, RetryDelayMaxMs + 1);
                    try
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    continue;
                }

                throw new TtsSpeakException(
                    lastKind,
                    plan.SelectionMode,
                    plan.Voice,
                    attempt,
                    ex.Message,
                    ex);
            }
        }

        throw new TtsSpeakException(
            lastKind,
            plan.SelectionMode,
            plan.Voice,
            MaxAttemptsPerVoice,
            last?.Message ?? "TTS synthesize failed.",
            last);
    }

    private bool IsCurrentSpeak(long speakId, CancellationTokenSource linked)
    {
        lock (_gate)
            return !_disposed && speakId == Volatile.Read(ref _speakId) && ReferenceEquals(_speakCts, linked);
    }

    private async Task PlayFileAsync(string path, string selectionMode, string voice, CancellationToken cancellationToken)
    {
        var endedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await RunOnDispatcherAsync(() =>
        {
            _player ??= new MediaPlayer();

            void CleanupHandlers()
            {
                _player.MediaOpened -= OnOpened;
                _player.MediaEnded -= OnEnded;
                _player.MediaFailed -= OnFailed;
            }

            void OnOpened(object? sender, EventArgs e)
            {
                _player.MediaOpened -= OnOpened;
                openedTcs.TrySetResult();
            }

            void OnEnded(object? sender, EventArgs e)
            {
                CleanupHandlers();
                endedTcs.TrySetResult();
            }

            void OnFailed(object? sender, ExceptionEventArgs e)
            {
                CleanupHandlers();
                var error = e.ErrorException ?? new InvalidOperationException("Media playback failed.");
                openedTcs.TrySetException(error);
                endedTcs.TrySetException(error);
            }

            _player.MediaOpened += OnOpened;
            _player.MediaEnded += OnEnded;
            _player.MediaFailed += OnFailed;
            _player.Open(new Uri(path, UriKind.Absolute));
        });

        using (cancellationToken.Register(() =>
        {
            _ = RunOnDispatcherAsync(() =>
            {
                try { _player?.Stop(); }
                catch { /* ignore */ }
            });
            openedTcs.TrySetCanceled(cancellationToken);
            endedTcs.TrySetCanceled(cancellationToken);
        }))
        {
            try
            {
                var openCompleted = await Task.WhenAny(
                        openedTcs.Task,
                        Task.Delay(MediaOpenTimeoutMs, cancellationToken))
                    .ConfigureAwait(false);

                if (openCompleted != openedTcs.Task)
                {
                    throw new TtsSpeakException(
                        TtsSpeakException.Playback,
                        selectionMode,
                        voice,
                        attempt: 0,
                        "Media open timed out.");
                }

                await openedTcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TtsSpeakException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new TtsSpeakException(
                    TtsSpeakException.Playback,
                    selectionMode,
                    voice,
                    attempt: 0,
                    ex.Message,
                    ex);
            }

            await RunOnDispatcherAsync(() =>
            {
                try { _player?.Play(); }
                catch (Exception ex)
                {
                    endedTcs.TrySetException(ex);
                }
            }).ConfigureAwait(false);

            try
            {
                await endedTcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not TtsSpeakException)
            {
                throw new TtsSpeakException(
                    TtsSpeakException.Playback,
                    selectionMode,
                    voice,
                    attempt: 0,
                    ex.Message,
                    ex);
            }
        }
    }

    private Task StopPlaybackCoreAsync() =>
        RunOnDispatcherAsync(() =>
        {
            if (_player is null)
                return;
            try { _player.Stop(); }
            catch { /* ignore */ }
            try { _player.Close(); }
            catch { /* ignore */ }
        });

    /// <summary>
    /// Run media work on the WPF dispatcher without deadlocking when the caller
    /// already owns that dispatcher (e.g. App.OnExit sync-over-async dispose).
    /// </summary>
    private Task RunOnDispatcherAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    /// <summary>
    /// Test seam: same reentrancy rule as production media marshaling.
    /// </summary>
    internal static Task RunOnDispatcherForTestsAsync(Dispatcher dispatcher, Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private static string CreateTempAudioPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "QuickTranslate", "tts");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".mp3");
    }

    private static void DeleteTempFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch { /* UI subscribers must not break TTS. */ }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(EdgeTtsService));
    }
}


