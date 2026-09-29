using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.HostedDesktopPublisherTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class HostedConnectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptOrUnapprovedStartupNeverCreatesSender(bool unapproved) => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            string path = Path.Combine(root, "pairing.private");
            var valid = new HostedPublisherConfigurationStore(path, new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            if (unapproved) await valid.SaveAsync(Configuration()); else await File.WriteAllTextAsync(path, "broken");
            var store = new HostedPublisherConfigurationStore(path, new CurrentUserDpapiSecretProtector(), HostedProductionOrigins.Approved);
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, _ => throw new InvalidOperationException("must not construct"));
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Assert.False(connection.CanCopy);
            Assert.Contains("could not be prepared", connection.StatusText, StringComparison.Ordinal);
            Assert.DoesNotContain("must not construct", connection.StatusText);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task HealthyPanelIsMinimalReadOnlyAccessibleAndCapabilitySafe() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, config => new HostedOverlayPublisher(config, new Server()));
            await using var coordinator = new SoulsTracker.Application.SerializedTrackerCoordinator(new SqliteTrackerStateRepository(root, "tracker.db"), connection);
            var vm = new DesktopTrackerViewModel(coordinator); vm.ConfigureHostedOverlay(connection);
            var window = new MainWindow { DataContext = vm, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
                await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
                await Idle();
                var text = Tree(window).OfType<TextBlock>().Select(x => x.Text).ToArray();
                Assert.Contains("Overlay", text);
                Assert.Contains("Your overlay URL is a private link. Anyone with it can view the overlay, so only share it where you need to.", text);
                Assert.DoesNotContain(text, value => value.Contains("browser source", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(text, value => value.Contains("publication", StringComparison.OrdinalIgnoreCase));
                Assert.Null(window.FindName("SetUpHostedOverlayButton"));
                Assert.Null(window.FindName("HostedConsentCheckBox"));
                Assert.Null(window.FindName("HostedHostTextBlock"));
                var panel = Tree(window).OfType<Border>().Single(x => AutomationProperties.GetName(x) == "Overlay connection panel");
                var panelContent = Assert.IsType<StackPanel>(panel.Child);
                Assert.Equal(3, panelContent.Children.OfType<FrameworkElement>().Count(x => x.IsVisible));
                var url = Tree(window).OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == "Overlay URL");
                Assert.True(url.IsReadOnly && url.Focusable);
                Assert.Equal(Configuration().BuildReadUrl(), url.Text);
                Assert.True(url.Focus());
                url.SelectAll();
                Assert.Equal(url.Text.Length, url.SelectionLength);
                var copy = (Button)window.FindName("CopyTotalDeathsOverlayUrlButton");
                Assert.Equal("Copy URL", AutomationProperties.GetName(copy));
                Assert.Equal("Copy URL", copy.ToolTip);
                Assert.Equal(string.Empty, copy.Content);
                Assert.True(copy.IsEnabled);
                string visible = string.Join(' ', text);
                Assert.DoesNotContain(new string('b', 64), visible);
                Assert.DoesNotContain(new string('c', 64), visible);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task ExistingPairingPublishesOnlyAcceptedCurrentStateAndCloseFencesWork() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            var server = new Server();
            HostedOverlayPublisher? sender = null;
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store,
                config => sender = new HostedOverlayPublisher(config, server));
            var state = RuntimePublicationSessionTests.Selected(GameId.EldenRing);
            await connection.InitializeAsync(state);
            var session = new RuntimePublicationSession(); session.SelectState(state);
            session.CompleteRead(session.BeginRead(state), state, RuntimeGameReadResult.Synced(new RuntimeGameObservation(
                state.SelectedGameId, 41, DateTimeOffset.UtcNow, EffectiveDeathTotalResult.SourceIdentityFor(state))), _ => { },
                accepted => connection.PublishAccepted(state, accepted));
            await WaitUntil(() => server.Value == "41" && sender!.Status == HostedPublisherStatus.Ready);
            Assert.True(connection.CanCopy);
            Assert.False(connection.CopyReadUrl(_ => throw new InvalidOperationException("private clipboard detail")));
            Assert.DoesNotContain("private clipboard detail", connection.StatusText + connection.CopyFeedbackText);
            Task firstClose = connection.DisposeAsync().AsTask();
            Assert.Same(firstClose, connection.DisposeAsync().AsTask());
            await firstClose;
            Assert.False(connection.CanCopy);
            Assert.Equal(HostedPublisherStatus.Stopped, sender!.Status);
        }
        finally { Directory.Delete(root, true); }
    });

    private static string NewRoot() { string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static IEnumerable<System.Windows.DependencyObject> Tree(System.Windows.DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
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
