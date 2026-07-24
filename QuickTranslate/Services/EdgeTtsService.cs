using System.IO;
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
    private readonly EdgeTtsClient _client;
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private CancellationTokenSource? _speakCts;
    private MediaPlayer? _player;
    private string? _currentTempFile;
    private long _speakId;
    private bool _isBusy;
    private bool _disposed;

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
        try
        {
            await StopPlaybackCoreAsync().ConfigureAwait(false);

            var normalized = TtsTextSelector.NormalizeForSpeech(text, maxChars: 0, out _);
            if (string.IsNullOrWhiteSpace(normalized))
                throw new InvalidOperationException("No speakable text.");

            var voice = TtsTextSelector.ResolveVoice(normalized, voiceOverride);
            var clampedRate = TtsTextSelector.ClampRate(rate);

            Logger.Info("Tts", "tts.speak.started", new Dictionary<string, object?>
            {
                ["text_len"] = normalized.Length,
                ["voice"] = voice,
                ["rate"] = clampedRate,
                ["speak_id"] = speakId,
                ["language_hint"] = languageHint
            });

            var audio = await _client.SynthesizeAsync(normalized, voice, clampedRate, linked.Token)
                .ConfigureAwait(false);
            if (audio.Length == 0)
                throw new InvalidOperationException("Empty audio payload.");

            tempPath = CreateTempAudioPath();
            await File.WriteAllBytesAsync(tempPath, audio, linked.Token).ConfigureAwait(false);

            lock (_gate)
            {
                if (!ReferenceEquals(_speakCts, linked))
                    return;
                _currentTempFile = tempPath;
            }

            await PlayFileAsync(tempPath, linked.Token).ConfigureAwait(false);

            if (IsCurrentSpeak(speakId, linked))
            {
                Logger.Info("Tts", "tts.speak.completed", new Dictionary<string, object?>
                {
                    ["duration_ms"] = sw.ElapsedMilliseconds,
                    ["speak_id"] = speakId,
                    ["audio_bytes"] = audio.Length
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
                    ["speak_id"] = speakId
                });
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            if (IsCurrentSpeak(speakId, linked))
            {
                Logger.Error("Tts", "tts.speak.failed", new Dictionary<string, object?>
                {
                    ["speak_id"] = speakId,
                    ["exception_type"] = ex.GetType().Name
                }, ex);
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
        await _dispatcher.InvokeAsync(() =>
        {
            if (_player is null)
                return;
            try { _player.Close(); }
            catch { /* ignore */ }
            _player = null;
        });
    }

    private bool IsCurrentSpeak(long speakId, CancellationTokenSource linked)
    {
        lock (_gate)
            return !_disposed && speakId == Volatile.Read(ref _speakId) && ReferenceEquals(_speakCts, linked);
    }

    private async Task PlayFileAsync(string path, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await _dispatcher.InvokeAsync(() =>
        {
            _player ??= new MediaPlayer();

            void OnEnded(object? sender, EventArgs e)
            {
                _player.MediaEnded -= OnEnded;
                _player.MediaFailed -= OnFailed;
                tcs.TrySetResult();
            }

            void OnFailed(object? sender, ExceptionEventArgs e)
            {
                _player.MediaEnded -= OnEnded;
                _player.MediaFailed -= OnFailed;
                tcs.TrySetException(
                    e.ErrorException ?? new InvalidOperationException("Media playback failed."));
            }

            _player.MediaEnded += OnEnded;
            _player.MediaFailed += OnFailed;
            _player.Open(new Uri(path, UriKind.Absolute));
            _player.Play();
        });

        using (cancellationToken.Register(() =>
        {
            _ = _dispatcher.InvokeAsync(() =>
            {
                try { _player?.Stop(); }
                catch { /* ignore */ }
            });
            tcs.TrySetCanceled(cancellationToken);
        }))
        {
            await tcs.Task.ConfigureAwait(false);
        }
    }

    private Task StopPlaybackCoreAsync() =>
        _dispatcher.InvokeAsync(() =>
        {
            if (_player is null)
                return;
            try { _player.Stop(); }
            catch { /* ignore */ }
            try { _player.Close(); }
            catch { /* ignore */ }
        }).Task;

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
