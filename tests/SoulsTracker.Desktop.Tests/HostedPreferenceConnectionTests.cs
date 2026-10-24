using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.HostedConnectionTests;
using static SoulsTracker.Desktop.Tests.HostedDesktopPublisherTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class HostedPreferenceConnectionTests
{
    [Fact]
    public async Task DisabledStartupNeverConstructsTransportOrReplaysPendingAndKeepsLocalProjection() => await OnDispatcher(async () =>
    {
        string root = Root();
        try
        {
            var preference = await DisabledPreference(root);
            var store = Store(root);
            await store.SaveAsync(ApprovedConfiguration());
            var pending = Pending(root);
            var request = HostedProvisioningState.Create("https://overlay.beingkairo.com", HostedProductionOrigins.Approved);
            await pending.SavePendingAsync(request);
            int senders = 0, clients = 0;
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store,
                _ => { senders++; throw new InvalidOperationException("sender forbidden"); }, pending,
                preferenceStore: preference, initiallyEnabled: false,
                createProvisioningClient: () => { clients++; throw new InvalidOperationException("client forbidden"); });
            await connection.InitializeAsync(PersistentTrackerState.Default);
            await connection.EnsureProvisionedAsync();
            await connection.RetryAsync();
            await connection.PublishAsync(new SoulsTracker.Application.TrackerStateChanged(PersistentTrackerState.Default,
                SoulsTracker.Application.TrackerCommandType.SetCheckForUpdatesOnStartup));
            Assert.False(connection.IsEnabled);
            Assert.Equal(0, senders);
            Assert.Equal(0, clients);
            Assert.Empty(connection.UrlText);
            Assert.False(connection.CanCopy);
            Assert.NotNull(await pending.LoadPendingAsync());
            Assert.NotNull(await store.LoadAsync());
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task DisableThenRestartDoesNotConstructSenderAndEnableReusesIdentity() => await OnDispatcher(async () =>
    {
        string root = Root();
        try
        {
            var preference = await DisabledPreference(root);
            var store = Store(root);
            await store.SaveAsync(ApprovedConfiguration());
            int senders = 0;
            await using (var first = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store,
                config => { senders++; return new HostedOverlayPublisher(config, new Server()); },
                preferenceStore: preference, initiallyEnabled: true))
            {
                await preference.SaveAsync(true);
                await first.InitializeAsync(PersistentTrackerState.Default);
                Assert.Equal(1, senders);
                Assert.Equal(ApprovedConfiguration().BuildReadUrl(), first.UrlText);
                await first.SetEnabledAsync(false);
                Assert.False(first.IsEnabled);
                Assert.Empty(first.UrlText);
                Assert.False(first.CanCopy);
                Assert.False(first.CopyReadUrl(_ => Assert.Fail("Disabled URL copied")));
                Assert.False(await preference.ResolveAsync(_ => Task.FromResult(true)));
                await first.EnsureProvisionedAsync();
                Assert.Equal(1, senders);
            }
            await using var second = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store,
                config => { senders++; return new HostedOverlayPublisher(config, new Server()); },
                preferenceStore: preference, initiallyEnabled: false);
            await second.InitializeAsync(PersistentTrackerState.Default);
            Assert.Equal(1, senders);
            Assert.Empty(second.UrlText);
            Assert.False(second.CanCopy);
            await second.SetEnabledAsync(true);
            Assert.True(second.IsEnabled);
            Assert.Equal(2, senders);
            Assert.Equal(ApprovedConfiguration().BuildReadUrl(), second.UrlText);
            Assert.True(second.CanCopy);
            Assert.Equal(ApprovedConfiguration().BuildReadUrl(), (await store.LoadAsync())!.BuildReadUrl());
            Assert.True(await preference.ResolveAsync(_ => Task.FromResult(false)));
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task DisableCancelsInFlightSenderBeforeItsNextNetworkBoundary() => await OnDispatcher(async () =>
    {
        string root = Root();
        try
        {
            var preference = await DisabledPreference(root);
            await preference.SaveAsync(true);
            var store = Store(root);
            await store.SaveAsync(ApprovedConfiguration());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int requests = 0;
            var server = new Server
            {
                BeforeSend = async ct =>
                {
                    Interlocked.Increment(ref requests);
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                },
            };
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store,
                config => new HostedOverlayPublisher(config, server), preferenceStore: preference, initiallyEnabled: true);
            await connection.InitializeAsync(PersistentTrackerState.Default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await connection.SetEnabledAsync(false).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, requests);
            Assert.False(await preference.ResolveAsync(_ => Task.FromResult(true)));
            await connection.PublishAsync(new SoulsTracker.Application.TrackerStateChanged(PersistentTrackerState.Default,
                SoulsTracker.Application.TrackerCommandType.SetCheckForUpdatesOnStartup));
            Assert.Equal(1, requests);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task PendingOnlyUpgradeRemainsQuietUntilEnabledThenPromotesWithoutCreate() => await OnDispatcher(async () =>
    {
        string root = Root();
        try
        {
            var preference = await DisabledPreference(root);
            var pending = Pending(root);
            var request = HostedProvisioningState.Create("https://overlay.beingkairo.com", HostedProductionOrigins.Approved)
                .Acknowledged(new string('a', 64));
            await pending.SavePendingAsync(request);
            int senders = 0, provisioning = 0;
            var store = Store(root);
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store,
                config => { senders++; return new HostedOverlayPublisher(config, new Server()); }, pending,
                preferenceStore: preference, initiallyEnabled: false,
                createProvisioningClient: () => new HostedOverlayProvisioningClient("https://overlay.beingkairo.com",
                    new RejectingHandler(() => provisioning++)));
            await connection.InitializeAsync(PersistentTrackerState.Default);
            Assert.Equal(0, senders);
            Assert.NotNull(await pending.LoadPendingAsync());
            await connection.SetEnabledAsync(true);
            Assert.Equal(1, senders);
            Assert.Equal(0, provisioning);
            Assert.Null(await pending.LoadPendingAsync());
            Assert.NotNull(await store.LoadAsync());
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task DisableDuringProvisioningCancelsRetryAndRetainsExactPendingIdentity() => await OnDispatcher(async () =>
    {
        string root = Root();
        try
        {
            var preference = await DisabledPreference(root);
            var pending = Pending(root);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int requests = 0, senders = 0;
            var store = Store(root);
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store,
                _ => { senders++; throw new InvalidOperationException("sender forbidden"); }, pending,
                preferenceStore: preference, initiallyEnabled: false,
                createProvisioningClient: () => new HostedOverlayProvisioningClient("https://overlay.beingkairo.com",
                    new WaitingHandler(async ct =>
                    {
                        requests++;
                        entered.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    })));
            await connection.InitializeAsync(PersistentTrackerState.Default);
            Task enable = connection.SetEnabledAsync(true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(connection.CanChangeEnabled);
            var original = (await pending.LoadPendingAsync())!.RequestId;
            await connection.SetEnabledAsync(false).WaitAsync(TimeSpan.FromSeconds(5));
            await enable;
            Assert.Equal(1, requests);
            Assert.Equal(0, senders);
            Assert.Equal(original, (await pending.LoadPendingAsync())!.RequestId);
            Assert.False(await preference.ResolveAsync(_ => Task.FromResult(true)));
        }
        finally { Directory.Delete(root, true); }
    });

    private sealed class WaitingHandler(Func<CancellationToken, Task> wait) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await wait(cancellationToken);
            throw new InvalidOperationException("Unexpected completion");
        }
    }

    private sealed class RejectingHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            throw new InvalidOperationException("Unexpected provisioning request");
        }
    }

    private static async Task<HostedOverlayPreferenceStore> DisabledPreference(string root)
    {
        var preference = new HostedOverlayPreferenceStore(root);
        Assert.False(await preference.ResolveAsync(_ => Task.FromResult(true)));
        return preference;
    }
    private static HostedPublisherConfigurationStore Store(string root) => new(
        Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), HostedProductionOrigins.Approved);
    private static HostedPublisherConfiguration ApprovedConfiguration() => HostedPublisherConfiguration.Create(
        "https://overlay.beingkairo.com", new string('a', 32), new string('b', 64), new string('c', 64), HostedProductionOrigins.Approved);
    private static HostedProvisioningStateStore Pending(string root) => new(
        Path.Combine(root, "pending.private"), new CurrentUserDpapiSecretProtector(), HostedProductionOrigins.Approved);
    private static string Root() { string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
}
