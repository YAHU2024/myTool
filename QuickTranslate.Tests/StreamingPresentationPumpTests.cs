using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class StreamingPresentationPumpTests
{
    [Fact]
    public async Task BurstBeforeFirstFrame_IsAppliedAsOneBatch()
    {
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new List<StreamingPresentationFrame>();
        await using var pump = new StreamingPresentationPump(
            (frame, _) =>
            {
                frames.Add(frame);
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(30),
            async (_, cancellationToken) =>
            {
                delayStarted.TrySetResult();
                await releaseDelay.Task.WaitAsync(cancellationToken);
            });

        Assert.True(pump.Publish("one"));
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pump.Publish(" two"));
        Assert.True(pump.Publish(" three"));

        var completion = pump.CompleteAsync();
        releaseDelay.TrySetResult();
        var stats = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        var frame = Assert.Single(frames);
        Assert.Equal("one two three", frame.Delta);
        Assert.Equal(3, frame.ChunkCount);
        Assert.Equal(3, stats.PublishedChunkCount);
        Assert.Equal(1, stats.AppliedFrameCount);
        Assert.Equal(2, stats.CoalescedChunkCount);
        Assert.True(stats.FirstFrameLatencyMs >= 0);
        Assert.True(stats.MaxFrameLatencyMs >= stats.FirstFrameLatencyMs);
        Assert.True(stats.AverageApplyDurationMs >= 0);
        Assert.True(stats.MaxApplyDurationMs >= stats.AverageApplyDurationMs);
    }

    [Fact]
    public async Task PublishWhileApplying_QueuesOnlyOneFollowingBatch()
    {
        var firstApplyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new List<StreamingPresentationFrame>();
        var concurrentApplies = 0;
        var maxConcurrentApplies = 0;
        await using var pump = new StreamingPresentationPump(
            async (frame, cancellationToken) =>
            {
                var concurrent = Interlocked.Increment(ref concurrentApplies);
                maxConcurrentApplies = Math.Max(maxConcurrentApplies, concurrent);
                frames.Add(frame);
                if (frames.Count == 1)
                {
                    firstApplyStarted.TrySetResult();
                    await releaseFirstApply.Task.WaitAsync(cancellationToken);
                }
                Interlocked.Decrement(ref concurrentApplies);
            },
            TimeSpan.Zero,
            (_, _) => Task.CompletedTask);

        Assert.True(pump.Publish("first"));
        await firstApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(pump.Publish(" second"));
        Assert.True(pump.Publish(" third"));
        var completion = pump.CompleteAsync();
        releaseFirstApply.TrySetResult();

        var stats = await completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Collection(
            frames,
            frame => Assert.Equal("first", frame.Delta),
            frame => Assert.Equal(" second third", frame.Delta));
        Assert.Equal(1, maxConcurrentApplies);
        Assert.Equal(3, stats.PublishedChunkCount);
        Assert.Equal(2, stats.AppliedFrameCount);
        Assert.Equal(1, stats.CoalescedChunkCount);
        Assert.True(stats.FirstFrameLatencyMs >= 0);
        Assert.True(stats.MaxFrameLatencyMs >= stats.FirstFrameLatencyMs);
    }

    [Fact]
    public async Task CompleteWithoutContent_DoesNotApplyEmptyFrame()
    {
        var applyCount = 0;
        await using var pump = new StreamingPresentationPump(
            (_, _) =>
            {
                applyCount++;
                return Task.CompletedTask;
            },
            TimeSpan.Zero);

        var stats = await pump.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, applyCount);
        Assert.Equal(0, stats.PublishedChunkCount);
        Assert.Equal(0, stats.AppliedFrameCount);
        Assert.Equal(0, stats.CoalescedChunkCount);
        Assert.Equal(0, stats.FirstFrameLatencyMs);
        Assert.Equal(0, stats.MaxFrameLatencyMs);
        Assert.Equal(0, stats.AverageApplyDurationMs);
        Assert.Equal(0, stats.MaxApplyDurationMs);
        Assert.False(pump.Publish("late"));
    }

    [Fact]
    public void CalculateNextFrameInterval_ExpandsForSlowFramesAndRecoversGradually()
    {
        var minimum = TimeSpan.FromMilliseconds(30);
        var maximum = TimeSpan.FromMilliseconds(120);

        var expanded = StreamingPresentationPump.CalculateNextFrameInterval(
            minimum,
            maximum,
            minimum,
            TimeSpan.FromMilliseconds(40));
        var recovered = StreamingPresentationPump.CalculateNextFrameInterval(
            minimum,
            maximum,
            expanded,
            TimeSpan.FromMilliseconds(5));

        Assert.Equal(TimeSpan.FromMilliseconds(60), expanded);
        Assert.Equal(TimeSpan.FromMilliseconds(55), recovered);
    }

    [Fact]
    public void CalculateNextFrameInterval_ClampsToConfiguredBounds()
    {
        var minimum = TimeSpan.FromMilliseconds(30);
        var maximum = TimeSpan.FromMilliseconds(120);

        Assert.Equal(
            maximum,
            StreamingPresentationPump.CalculateNextFrameInterval(
                minimum,
                maximum,
                TimeSpan.FromMilliseconds(110),
                TimeSpan.FromMilliseconds(200)));
        Assert.Equal(
            minimum,
            StreamingPresentationPump.CalculateNextFrameInterval(
                minimum,
                maximum,
                minimum,
                TimeSpan.FromMilliseconds(1)));
    }
}
