using System.ComponentModel;
using System.IO;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Controls;
using SoulsTracker.Desktop;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

public sealed class TrayLifecycleTests
{
    [Fact]
    public void MinimizeAndCloseHideWithoutStoppingBackgroundWorkAndRestoreKeepsSameSession()
    {
        int hidden = 0, restored = 0, exited = 0;
        var messages = new List<string>();
        int tracking = 0;
        var lifecycle = new DesktopTrayLifecycle(() => hidden++, () => restored++, () => exited++, messages.Add, () => true)
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
        var lifecycle = new DesktopTrayLifecycle(() => hidden++, () => restored++, () => exited++, messages.Add, () => true);
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
    public void UncheckedCloseRequestsOrderedExitWithoutHidingEvenWhenTrayIsUnavailable()
    {
        int hidden = 0, restored = 0, exited = 0;
        bool enabled = false;
        var lifecycle = new DesktopTrayLifecycle(() => hidden++, () => restored++, () => exited++, _ => { }, () => enabled);
        var closing = new CancelEventArgs();
        lifecycle.Closing(closing);
        Assert.True(closing.Cancel);
        Assert.Equal(0, hidden);
        Assert.Equal(0, restored);
        Assert.Equal(1, exited);
        enabled = true;
        lifecycle.Closing(new CancelEventArgs());
        Assert.Equal(1, exited);
    }

    [Fact]
    public Task PreferenceOnlyChangesAfterSuccessfulSaveAndCanBeTurnedOffAgain() => HostedConnectionTests.OnDispatcher(async () =>
    {
        await using var coordinator = new SerializedTrackerCoordinator(new MemoryRepository(GameId.DemonsSouls), new NullPublisher());
        var vm = new DesktopTrackerViewModel(coordinator);
        Assert.False(vm.MinimizeToTrayOnClose);
        vm.ConfigureTrayClosePreference(false, enabled => enabled ? Task.CompletedTask : Task.FromException(new IOException("Save failed")));
        await vm.SetMinimizeToTrayOnCloseAsync(true);
        Assert.True(vm.MinimizeToTrayOnClose);
        await vm.SetMinimizeToTrayOnCloseAsync(false);
        Assert.True(vm.MinimizeToTrayOnClose);
        Assert.NotEmpty(vm.TrayClosePreferenceStatus);
        vm.ConfigureTrayClosePreference(true, _ => Task.CompletedTask);
        await vm.SetMinimizeToTrayOnCloseAsync(false);
        Assert.False(vm.MinimizeToTrayOnClose);
        Assert.Empty(vm.TrayClosePreferenceStatus);
    });

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
    public Task SettingsExposeAccessibleClosePreferenceAndNoWindowExit() => HostedConnectionTests.OnDispatcher(() =>
    {
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var footer = (Hyperlink)window.FindName("BeingKairoAttributionHyperlink");
            Assert.Single(((TextBlock)footer.Parent).Inlines.OfType<Hyperlink>());
            var close = (CheckBox)window.FindName("MinimizeToTrayOnCloseCheckBox");
            var updates = (CheckBox)window.FindName("CheckForUpdatesOnStartupCheckBox");
            Assert.Equal("Minimize to tray on close", AutomationProperties.GetName(close));
            Assert.Equal("Minimize to tray on close", close.Content);
            Assert.True(close.Focusable);
            Assert.False(close.IsChecked);
            Assert.Same(updates.Parent, close.Parent);
            Assert.Equal(((StackPanel)updates.Parent).Children.IndexOf(updates) + 1, ((StackPanel)close.Parent).Children.IndexOf(close));
            Assert.Equal("OPTIONS", ((HeaderedContentControl)window.FindName("UpdatesSectionHeader")).Header);
            Assert.Equal("TEXT EXPORT", ((HeaderedContentControl)window.FindName("StreamingTextExportSectionHeader")).Header);
            Assert.Null(window.FindName("UpdatesHelpButton"));
            Assert.Equal("Check for updates on startup", updates.Content);
            Assert.Equal("Just a simple death tracker.", ((TextBlock)window.FindName("HeaderSubtitleTextBlock")).Text);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    private sealed class DuplicateFactory : IDesktopSingleInstanceLeaseFactory
    {
        public DesktopSingleInstanceLeaseAcquisition Acquire() => DesktopSingleInstanceLeaseAcquisition.AlreadyRunning();
    }
}
