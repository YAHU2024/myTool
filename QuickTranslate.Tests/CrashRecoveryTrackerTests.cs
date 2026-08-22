using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class CrashRecoveryTrackerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"qt_recovery_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }
        catch { }
    }

    [Fact]
    public void CleanRun_DoesNotCreateRecoveryEvent()
    {
        var tracker = new CrashRecoveryTracker(_directory);
        Assert.Null(tracker.StartRun("1.0", "X64", DateTimeOffset.UtcNow));
        tracker.MarkClean();

        var next = new CrashRecoveryTracker(_directory);
        Assert.Null(next.StartRun("1.0", "X64", DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public void UncleanRun_CreatesOnePendingEventOnNextStart()
    {
        var first = new CrashRecoveryTracker(_directory);
        first.StartRun("1.0", "X64", DateTimeOffset.UtcNow);

        var next = new CrashRecoveryTracker(_directory);
        var recovery = next.StartRun("1.0", "X64", DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.NotNull(recovery);
        Assert.Equal(RecoveryPromptState.Pending, recovery!.PromptState);
        Assert.Equal("1.0", recovery.AppVersion);
        Assert.Null(next.StartRun("1.0", "X64", DateTimeOffset.UtcNow.AddMinutes(2)));
    }

    [Fact]
    public void PromptState_IsPersistedAndDoesNotRepeatForSameRun()
    {
        var first = new CrashRecoveryTracker(_directory);
        first.StartRun("1.0", "X64", DateTimeOffset.UtcNow);
        var next = new CrashRecoveryTracker(_directory);
        next.StartRun("1.0", "X64", DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.NotNull(next.MarkShown());
        Assert.Null(next.PendingEvent);

        var third = new CrashRecoveryTracker(_directory);
        var newEvent = third.StartRun("1.0", "X64", DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.NotNull(newEvent);
        Assert.Equal(RecoveryPromptState.Pending, newEvent!.PromptState);
    }

    [Fact]
    public void ExistingPendingEvent_IsPreservedUntilItIsHandled()
    {
        var first = new CrashRecoveryTracker(_directory);
        first.StartRun("1.0", "X64", DateTimeOffset.UtcNow);

        var second = new CrashRecoveryTracker(_directory);
        var original = second.StartRun("1.0", "X64", DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.NotNull(original);

        var third = new CrashRecoveryTracker(_directory);
        var samePending = third.StartRun("1.0", "X64", DateTimeOffset.UtcNow.AddMinutes(2));

        Assert.NotNull(samePending);
        Assert.Equal(original!.RunId, samePending!.RunId);
        Assert.Equal(RecoveryPromptState.Pending, samePending.PromptState);
    }
}
