using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Controls;
using SoulsTracker.Desktop;

namespace SoulsTracker.Desktop.Tests;

public sealed class TrayLifecycleTests
{
    [Fact]
    public void MinimizeAndCloseHideWithoutStoppingBackgroundWorkAndRestoreKeepsSameSession()
    {
        int hidden = 0, restored = 0, exited = 0;
        var messages = new List<string>();
        int tracking = 0;
        var lifecycle = new DesktopTrayLifecycle(() => hidden++, () => restored++, () => exited++, messages.Add)
        { TrayAvailable = true };
        lifecycle.Minimized();
        tracking++;
        var firstClose = new CancelEventArgs();
        lifecycle.Closing(firstClose);
        var secondClose = new CancelEventArgs();
        lifecycle.Closing(secondClose);
        lifecycle.Restore();
        Assert.True(firstClose.Cancel);
        Assert.True(secondClose.Cancel);
        Assert.Equal(3, hidden);
        Assert.Equal(1, tracking);
        Assert.Equal(1, restored);
        Assert.Equal(0, exited);
        Assert.Single(messages);
    }

    [Fact]
    public void IconFailureLeavesWindowUsableAndExitIsIdempotent()
    {
        int hidden = 0, restored = 0, exited = 0;
        var messages = new List<string>();
        var lifecycle = new DesktopTrayLifecycle(() => hidden++, () => restored++, () => exited++, messages.Add);
        lifecycle.Minimized();
        var closing = new CancelEventArgs();
        lifecycle.Closing(closing);
        Assert.True(closing.Cancel);
        Assert.Equal(0, hidden);
        Assert.Equal(2, restored);
        Assert.Single(messages);
        lifecycle.Exit();
        lifecycle.Exit();
        lifecycle.Restore();
        Assert.Equal(1, exited);
        Assert.Equal(2, restored);
    }

    [Fact]
    public async Task DuplicateLaunchSignalsOwnerWithoutComposingAndTimeoutFailsClosed()
    {
        string name = $"SoulsTracker.Desktop.Tests.{Guid.NewGuid():N}";
        int restored = 0, composed = 0;
        using var pipe = new DesktopActivationPipe(name, () => Interlocked.Increment(ref restored));
        pipe.Start();
        using var controller = new DesktopStartupController(new DuplicateFactory(),
            () => DesktopActivationPipe.RequestRestoreAsync(name, TimeSpan.FromSeconds(2)));
        DesktopStartupDecision decision = await controller.StartAsync(() => { composed++; return Task.CompletedTask; });
        Assert.Equal(DesktopStartupStatus.ActivatedExisting, decision.Status);
        Assert.Equal(0, composed);
        Assert.Equal(1, restored);
        Assert.True(await DesktopActivationPipe.RequestRestoreAsync(name, TimeSpan.FromSeconds(2)));
        Assert.Equal(2, restored);
        pipe.Dispose();
        Assert.False(await DesktopActivationPipe.RequestRestoreAsync(name, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task MissingListenerNeverAllowsDuplicateComposition()
    {
        string name = $"SoulsTracker.Desktop.Tests.{Guid.NewGuid():N}";
        int composed = 0;
        using var controller = new DesktopStartupController(new DuplicateFactory(),
            () => DesktopActivationPipe.RequestRestoreAsync(name, TimeSpan.FromMilliseconds(100)));
        var decision = await controller.StartAsync(() => { composed++; return Task.CompletedTask; });
        Assert.Equal(DesktopStartupStatus.AlreadyRunning, decision.Status);
        Assert.Equal(0, composed);
    }

    [Fact]
    public Task InWindowExitIsKeyboardReachableAndNamed() => HostedConnectionTests.OnDispatcher(() =>
    {
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var footer = (Hyperlink)window.FindName("BeingKairoAttributionHyperlink");
            var exit = ((TextBlock)footer.Parent).Inlines.OfType<Hyperlink>().First();
            Assert.Equal("Exit SoulsTracker", AutomationProperties.GetName(exit));
            int requests = 0;
            window.ExitRequested += () => requests++;
            exit.RaiseEvent(new System.Windows.RoutedEventArgs(Hyperlink.ClickEvent));
            Assert.Equal(1, requests);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    private sealed class DuplicateFactory : IDesktopSingleInstanceLeaseFactory
    {
        public DesktopSingleInstanceLeaseAcquisition Acquire() => DesktopSingleInstanceLeaseAcquisition.AlreadyRunning();
    }
}
