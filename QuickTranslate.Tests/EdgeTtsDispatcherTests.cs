using System.Windows.Threading;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public class EdgeTtsDispatcherTests
{
    [Fact]
    public async Task RunOnDispatcher_WhenAlreadyOnDispatcher_RunsInlineWithoutPump()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var ran = false;

        // Same-thread reentrancy must complete even if the dispatcher is not pumping
        // (this is the OnExit sync-over-async failure mode).
        await EdgeTtsService.RunOnDispatcherForTestsAsync(dispatcher, () => ran = true)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(ran);
    }

    [Fact]
    public async Task DisposeAsync_OnOwningDispatcher_CompletesWithoutDeadlock()
    {
        var service = new EdgeTtsService(dispatcher: Dispatcher.CurrentDispatcher);

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        // Second dispose is a no-op and must also return.
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }
}
