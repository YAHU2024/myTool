using System.Diagnostics;
using System.Text;

namespace QuickTranslate.Core;

internal sealed record StreamingPresentationFrame(
    string Delta,
    int ChunkCount,
    long PublishedChunkCount,
    long FirstPublishedTimestamp);

internal sealed record StreamingPresentationStats(
    long PublishedChunkCount,
    int AppliedFrameCount,
    long CoalescedChunkCount,
    double FirstFrameLatencyMs,
    double MaxFrameLatencyMs);

/// <summary>
/// Coalesces transport deltas into bounded presentation frames. At most one
/// frame is applied at a time, while newer deltas remain in one shared buffer.
/// </summary>
internal sealed class StreamingPresentationPump : IAsyncDisposable
{
    public static readonly TimeSpan DefaultFrameInterval = TimeSpan.FromMilliseconds(30);

    private readonly object _sync = new();
    private readonly StringBuilder _pendingDelta = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Func<StreamingPresentationFrame, CancellationToken, Task> _applyAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeSpan _frameInterval;
    private readonly TaskCompletionSource<StreamingPresentationStats> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _runLoop;

    private int _pendingChunkCount;
    private long _pendingFirstTimestamp;
    private long _publishedChunkCount;
    private int _appliedFrameCount;
    private long _coalescedChunkCount;
    private double _firstFrameLatencyMs;
    private double _maxFrameLatencyMs;
    private bool _wakeScheduled;
    private bool _completionRequested;
    private bool _disposed;

    public StreamingPresentationPump(
        Func<StreamingPresentationFrame, CancellationToken, Task> applyAsync,
        TimeSpan? frameInterval = null)
        : this(applyAsync, frameInterval, Task.Delay)
    {
    }

    internal StreamingPresentationPump(
        Func<StreamingPresentationFrame, CancellationToken, Task> applyAsync,
        TimeSpan? frameInterval,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(applyAsync);
        ArgumentNullException.ThrowIfNull(delayAsync);

        _applyAsync = applyAsync;
        _delayAsync = delayAsync;
        _frameInterval = frameInterval ?? DefaultFrameInterval;
        if (_frameInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(frameInterval));

        _runLoop = RunAsync(_disposeCts.Token);
    }

    public bool Publish(string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return true;

        var shouldWake = false;
        lock (_sync)
        {
            if (_disposed || _completionRequested)
                return false;

            if (_pendingChunkCount == 0)
                _pendingFirstTimestamp = Stopwatch.GetTimestamp();
            _pendingDelta.Append(delta);
            _pendingChunkCount++;
            _publishedChunkCount++;
            if (!_wakeScheduled)
            {
                _wakeScheduled = true;
                shouldWake = true;
            }
        }

        if (shouldWake)
            _wakeSignal.Release();
        return true;
    }

    public Task<StreamingPresentationStats> CompleteAsync()
    {
        var shouldWake = false;
        lock (_sync)
        {
            if (_disposed)
                return Task.FromCanceled<StreamingPresentationStats>(new CancellationToken(canceled: true));

            _completionRequested = true;
            if (!_wakeScheduled)
            {
                _wakeScheduled = true;
                shouldWake = true;
            }
        }

        if (shouldWake)
            _wakeSignal.Release();
        return _completion.Task;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _wakeSignal.WaitAsync(cancellationToken).ConfigureAwait(false);

                bool completing;
                lock (_sync)
                    completing = _completionRequested;
                if (!completing && _frameInterval > TimeSpan.Zero)
                    await _delayAsync(_frameInterval, cancellationToken).ConfigureAwait(false);

                StreamingPresentationFrame? frame = null;
                lock (_sync)
                {
                    _wakeScheduled = false;
                    if (_pendingChunkCount > 0)
                    {
                        frame = new StreamingPresentationFrame(
                            _pendingDelta.ToString(),
                            _pendingChunkCount,
                            _publishedChunkCount,
                            _pendingFirstTimestamp);
                        _pendingDelta.Clear();
                        _pendingChunkCount = 0;
                        _pendingFirstTimestamp = 0;
                    }
                }

                if (frame is not null)
                {
                    await _applyAsync(frame, cancellationToken).ConfigureAwait(false);
                    var latencyMs = Stopwatch.GetElapsedTime(frame.FirstPublishedTimestamp).TotalMilliseconds;
                    lock (_sync)
                    {
                        _appliedFrameCount++;
                        _coalescedChunkCount += frame.ChunkCount - 1;
                        if (_appliedFrameCount == 1)
                            _firstFrameLatencyMs = latencyMs;
                        _maxFrameLatencyMs = Math.Max(_maxFrameLatencyMs, latencyMs);
                    }
                }

                StreamingPresentationStats? stats = null;
                lock (_sync)
                {
                    if (_completionRequested && _pendingChunkCount == 0 && !_wakeScheduled)
                    {
                        stats = new StreamingPresentationStats(
                            _publishedChunkCount,
                            _appliedFrameCount,
                            _coalescedChunkCount,
                            _firstFrameLatencyMs,
                            _maxFrameLatencyMs);
                    }
                }

                if (stats is not null)
                {
                    _completion.TrySetResult(stats);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _disposeCts.Cancel();
        try
        {
            await _runLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _wakeSignal.Dispose();
        _disposeCts.Dispose();
    }
}
