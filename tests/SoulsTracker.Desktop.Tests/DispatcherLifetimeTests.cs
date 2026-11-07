using System.Windows.Threading;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class DispatcherLifetimeTests
{
    [Fact]
    public async Task WaitsForQueuedCleanupBeforeReturning()
    {
        bool cleaned = false;
        await HostedConnectionTests.OnDispatcher(() =>
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(() => cleaned = true, DispatcherPriority.Background);
            return Task.CompletedTask;
        });
        Assert.True(cleaned);
    }

    [Fact]
    public async Task PreservesTestFailureThroughDispatcherShutdown()
    {
        var expected = new InvalidOperationException("synthetic test failure");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => HostedConnectionTests.OnDispatcher(async () =>
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            throw expected;
        }));
        Assert.Same(expected, actual);
    }
}
