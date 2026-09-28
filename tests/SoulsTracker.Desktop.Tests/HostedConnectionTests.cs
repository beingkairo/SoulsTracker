using System.IO;
using System.Windows.Threading;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.HostedDesktopPublisherTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class HostedConnectionTests
{
    [Fact]
    public async Task FailedRemoveRetainsProtectedPairingAndCloseFencesReconnect() => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "private.bin");
            var store = new HostedPublisherConfigurationStore(target, new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            int created = 0;
            HostedOverlayPublisher? sender = null;
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, config =>
            {
                created++;
                return sender = new HostedOverlayPublisher(config, new Server { BeforeSend = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct) });
            });
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            await using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.True(connection.CanCopy);
                await connection.RemoveAsync(true);
                Assert.True(connection.CanCopy);
            }
            Assert.Equal(1, created);
            Assert.NotNull(await store.LoadAsync());
            Assert.False(connection.CopyReadUrl(_ => throw new InvalidOperationException("synthetic")));
            Assert.DoesNotContain("synthetic", connection.StatusText);
            Task reconnect = connection.ReconnectAsync();
            await connection.ReconnectAsync();
            connection.StopSetup();
            Task firstClose = connection.DisposeAsync().AsTask();
            Assert.Same(firstClose, connection.DisposeAsync().AsTask());
            await firstClose; await reconnect;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, created);
            Assert.False(connection.CanCopy);
            Assert.Equal(HostedPublisherStatus.Stopped, sender!.Status);
        }
        finally { Directory.Delete(root, true); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptOrUnapprovedStartupNeverCreatesSender(bool unapproved) => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "pairing.private");
            var valid = new HostedPublisherConfigurationStore(path, new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            if (unapproved) await valid.SaveAsync(Configuration()); else await File.WriteAllTextAsync(path, "broken");
            var store = new HostedPublisherConfigurationStore(path, new CurrentUserDpapiSecretProtector(), HostedProductionOrigins.Approved);
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, _ => throw new InvalidOperationException("must not construct"));
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Assert.False(connection.CanCopy);
            Assert.False(connection.CanReconnect);
            Assert.Contains("failed", connection.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain("must not construct", connection.StatusText);
            Assert.True(connection.CanRemove);
            await using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                await connection.RemoveAsync(true);
            Assert.True(connection.CanRemove);
            await connection.RemoveAsync(true);
            Assert.False(connection.CanRemove);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task ActualConnectionControlsNeverDisplayCapabilitiesAndDisableCopyWithoutPairing() => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, config => new HostedOverlayPublisher(config, new Server()));
            await using var coordinator = new SoulsTracker.Application.SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), connection);
            var vm = new DesktopTrackerViewModel(coordinator);
            vm.ConfigureHostedOverlay(connection);
            var window = new MainWindow { DataContext = vm, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                ((System.Windows.Controls.TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
                await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var guidance = ConnectionText(window).Single(text => text.StartsWith("Add this URL", StringComparison.Ordinal));
                Assert.Equal("Add this URL as a browser source in your streaming software.", guidance);
                Assert.Contains("Overlay", ConnectionText(window));
                Assert.Contains("SoulsTracker sends your displayed death count and overlay appearance online so you can use the overlay in your streaming software.", ConnectionText(window));
                Assert.Contains("Your overlay URL is a private link. Anyone with it can view the overlay, so only share it where you need to.", ConnectionText(window));
                Assert.Contains("https://overlay.beingkairo.com", HostedProductionOrigins.Approved);
                Assert.DoesNotContain("No production host is authorized in this build.", guidance);
                var copy = (System.Windows.Controls.Button)window.FindName("CopyTotalDeathsOverlayUrlButton");
                var setup = (System.Windows.Controls.Button)window.FindName("SetUpHostedOverlayButton");
                var consent = (System.Windows.Controls.CheckBox)window.FindName("HostedConsentCheckBox");
                Assert.Equal("I agree to online overlay publication", Assert.IsType<System.Windows.Controls.TextBlock>(consent.Content).Text);
                Assert.Equal("Copy URL", copy.Content);
                Assert.Equal("Set up overlay", setup.Content);
                Assert.True(copy.IsEnabled);
                Assert.False(setup.IsEnabled);
                consent.IsChecked = true;
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(setup.IsEnabled);
                var host = (System.Windows.Controls.TextBlock)window.FindName("HostedHostTextBlock");
                Assert.Equal(Configuration().DisplayOrigin, host.Text);
                Assert.DoesNotContain(new string('b', 64), host.Text);
                string visibleText = string.Join(" ", ConnectionText(window));
                Assert.DoesNotContain(new string('b', 64), visibleText);
                Assert.DoesNotContain(new string('c', 64), visibleText);
                Assert.Null(window.FindName("TotalDeathsOverlayUrlTextBox"));
                await connection.DisposeAsync();
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(copy.IsEnabled);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task ExistingPairingReconnectSeedsOnlyAcceptedCurrentState() => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            var servers = new List<Server>();
            var senders = new List<HostedOverlayPublisher>();
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, config =>
            {
                var server = new Server(); servers.Add(server);
                var sender = new HostedOverlayPublisher(config, server); senders.Add(sender); return sender;
            });
            var state = RuntimePublicationSessionTests.Selected(GameId.EldenRing);
            await connection.InitializeAsync(state);
            Assert.True(connection.CanCopy);
            var session = new RuntimePublicationSession(); session.SelectState(state);
            session.CompleteRead(session.BeginRead(state), state, RuntimeGameReadResult.Synced(new RuntimeGameObservation(
                state.SelectedGameId, 41, DateTimeOffset.UtcNow, EffectiveDeathTotalResult.SourceIdentityFor(state))), _ => { },
                accepted => connection.PublishAccepted(state, accepted));
            await WaitUntil(() => servers[0].Value == "41" && senders[0].Status == HostedPublisherStatus.Ready);
            await WaitUntil(() => connection.StatusText.Contains("acknowledged", StringComparison.Ordinal));
            Assert.True(connection.CanCopy);
            string? copied = null;
            Assert.True(connection.CopyReadUrl(value => copied = value));
            Assert.DoesNotContain(new string('c', 64), copied!);
            Assert.DoesNotContain(new string('b', 64), connection.Host + connection.StatusText);
            await connection.ReconnectAsync();
            Assert.Equal(HostedPublisherStatus.Stopped, senders[0].Status);
            await WaitUntil(() => servers[1].Value == "41" && senders[1].Status == HostedPublisherStatus.Ready);
            await connection.RemoveAsync(false);
            Assert.True(connection.CanCopy);
            await connection.RemoveAsync(true);
            Assert.False(connection.CanCopy);
            Assert.Null(await store.LoadAsync());

        }
        finally { Directory.Delete(root, true); }
    });

    private static IEnumerable<string> ConnectionText(System.Windows.DependencyObject root)
    {
        if (root is System.Windows.Controls.TextBlock text) yield return text.Text;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (string value in ConnectionText(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return value;
    }

    internal static async Task OnDispatcher(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    try { await test(); }
                    // The body has drained its owned work. Shut down on this
                    // thread rather than queueing behind pending UI work.
                    finally { dispatcher.InvokeShutdown(); }
                    completion.SetResult();
                }
                catch (Exception error) { completion.SetException(error); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { Assert.True(thread.Join(TimeSpan.FromSeconds(5))); }
    }
}
