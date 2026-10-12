using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using QuickTranslate.Helpers;

namespace QuickTranslate.Core;

internal sealed record ClipboardRestoreRequest(
    long RequestId,
    string OriginalText,
    uint CopiedSequence,
    DateTimeOffset EnqueuedAt);

/// <summary>
/// Restores text captured before a simulated copy without holding up translation.
/// The worker is deliberately isolated from the WPF dispatcher because clipboard
/// owners can block or reject SetText while they are shutting down.
/// </summary>
internal sealed class ClipboardRestoreCoordinator : IDisposable
{
    internal const int MaxAttempts = 5;
    internal const int MaxDurationMilliseconds = 1500;
    private const int MaxPendingRequests = 4;

    private readonly BlockingCollection<ClipboardRestoreRequest> _requests =
        new(new ConcurrentQueue<ClipboardRestoreRequest>(), MaxPendingRequests);
    private readonly object _stateSync = new();
    private readonly Thread _worker;
    private long _nextRequestId;
    private ClipboardRestoreRequest? _pendingRestore;
    private int _disposed;

    public ClipboardRestoreCoordinator()
    {
        _worker = new Thread(WorkerProc)
        {
            IsBackground = true,
            Name = "Clipboard_Restore_Worker"
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public bool TryEnqueue(string originalText, uint copiedSequence)
    {
        if (string.IsNullOrEmpty(originalText) || copiedSequence == 0 ||
            Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        ClipboardRestoreRequest? request = null;
        ClipboardRestoreRequest? previousRestore = null;
        try
        {
            request = new ClipboardRestoreRequest(
                Interlocked.Increment(ref _nextRequestId),
                originalText,
                copiedSequence,
                DateTimeOffset.UtcNow);
            lock (_stateSync)
            {
                previousRestore = _pendingRestore;
                _pendingRestore = request;
                var added = _requests.TryAdd(request);
                if (!added)
                    _pendingRestore = previousRestore;
                return added;
            }
        }
        catch (InvalidOperationException)
        {
            lock (_stateSync)
            {
                if (request is not null && _pendingRestore?.RequestId == request.RequestId)
                    _pendingRestore = previousRestore;
            }
            return false;
        }
    }

    private void WorkerProc()
    {
        try
        {
            foreach (var request in _requests.GetConsumingEnumerable())
                Restore(request);
        }
        catch (ObjectDisposedException)
        {
            // Disposal is best effort and must never affect application exit.
        }
    }

    private void Restore(ClipboardRestoreRequest request)
    {
        var watch = Stopwatch.StartNew();
        var attempt = 0;
        Logger.Debug("ClipboardHelper", "clipboard.restore_started", new
        {
            copied_sequence = request.CopiedSequence,
            queued_ms = (DateTimeOffset.UtcNow - request.EnqueuedAt).TotalMilliseconds
        });

        while (attempt < MaxAttempts && watch.ElapsedMilliseconds < MaxDurationMilliseconds)
        {
            var currentSequence = Win32Api.GetClipboardSequenceNumber();
            if (currentSequence != request.CopiedSequence)
            {
                Logger.Debug("ClipboardHelper", "clipboard.restore_skipped_sequence_changed", new
                {
                    copied_sequence = request.CopiedSequence,
                    current_sequence = currentSequence,
                    attempt,
                    duration_ms = watch.Elapsed.TotalMilliseconds
                });
                ClearPending(request);
                return;
            }

            attempt++;
            try
            {
                Clipboard.SetText(request.OriginalText);
                var afterSequence = Win32Api.GetClipboardSequenceNumber();
                Logger.Debug("ClipboardHelper", "clipboard.restore_succeeded", new
                {
                    attempt,
                    duration_ms = watch.Elapsed.TotalMilliseconds,
                    sequence_changed_after_set = afterSequence != request.CopiedSequence
                });
                ClearPending(request);
                return;
            }
            catch (Exception ex)
            {
                Logger.Debug("ClipboardHelper", "clipboard.restore_attempt_failed", new
                {
                    attempt,
                    error_type = ex.GetType().Name
                });
            }

            if (attempt < MaxAttempts && watch.ElapsedMilliseconds < MaxDurationMilliseconds)
                Thread.Sleep(GetRetryDelayMilliseconds(attempt));
        }

        Logger.Warn("ClipboardHelper", "clipboard.restore_failed", new
        {
            attempt_count = attempt,
            duration_ms = watch.Elapsed.TotalMilliseconds
        });
        ClearPending(request);
    }

    public bool TryGetPendingOriginalText(uint currentSequence, out string? originalText)
    {
        lock (_stateSync)
        {
            if (_pendingRestore is { } pending && pending.CopiedSequence == currentSequence)
            {
                originalText = pending.OriginalText;
                return true;
            }
        }

        originalText = null;
        return false;
    }

    private void ClearPending(ClipboardRestoreRequest request)
    {
        lock (_stateSync)
        {
            if (_pendingRestore?.RequestId == request.RequestId)
                _pendingRestore = null;
        }
    }

    internal static int GetRetryDelayMilliseconds(int attempt) =>
        attempt switch
        {
            1 => 100,
            2 => 250,
            3 => 500,
            _ => 0
        };

    internal static bool ShouldQueue(string? originalText, uint copiedSequence, bool restoreRequested) =>
        restoreRequested && !string.IsNullOrEmpty(originalText) && copiedSequence != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _requests.CompleteAdding();
        // Do not wait indefinitely for a native clipboard call during shutdown.
        if (Thread.CurrentThread != _worker)
            _worker.Join(200);
        _requests.Dispose();
    }
}
