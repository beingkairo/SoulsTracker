using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.HostedConnectionTests;
using static SoulsTracker.Desktop.Tests.HostedDesktopPublisherTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class HostedSetupConnectionTests
{
    private const string Origin = "https://overlay.beingkairo.com";
    private static readonly string OverlayId = "a1" + new string('0', 62);

    [Fact]
    public async Task FirstUseProtectsPendingBeforeNetworkPromotesAndCopiesReadOnlyUrl() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            string activePath = Path.Combine(root, "active.private"), pendingPath = Path.Combine(root, "pending.private");
            bool protectedBeforeSend = false;
            int requests = 0;
            var handler = new Handler(async (request, cancellationToken) =>
            {
                Interlocked.Increment(ref requests);
                protectedBeforeSend = File.Exists(pendingPath) && (await File.ReadAllBytesAsync(pendingPath, cancellationToken)).Length > 0;
                Assert.Equal($"{Origin}/api/v1/overlays", request.RequestUri!.AbsoluteUri);
                Assert.Null(request.Headers.Authorization);
                return Ack();
            });
            var active = ActiveStore(activePath);
            var pending = PendingStore(root);
            await using var connection = Connection(active, pending, handler);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Assert.False(connection.CanCopy);
            Assert.False(File.Exists(pendingPath));
            Assert.Equal(0, requests);
            await connection.EnsureProvisionedAsync();
            Assert.True(protectedBeforeSend);
            Assert.Equal(1, requests);
            Assert.True(connection.CanCopy);
            Assert.NotNull(await active.LoadAsync());
            Assert.Null(await pending.LoadPendingAsync());
            string? copied = null;
            Assert.True(connection.CopyReadUrl(value => copied = value));
            Assert.StartsWith(Origin + "/overlay/#id=" + OverlayId + "&read=", copied, StringComparison.Ordinal);
            Assert.Equal("URL copied", connection.CopyFeedbackText);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task LostResponseRetriesExactProtectedRequest() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var requests = new List<string>();
            int attempt = 0;
            var handler = new Handler(async (request, cancellationToken) =>
            {
                requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (Interlocked.Increment(ref attempt) == 1) throw new HttpRequestException("lost response");
                return Ack();
            });
            await using var connection = Connection(ActiveStore(Path.Combine(root, "active.private")), PendingStore(root), handler);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            await connection.EnsureProvisionedAsync();
            Assert.Equal(2, requests.Count);
            Assert.Equal(requests[0], requests[1]);
            Assert.True(connection.CanCopy);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task RestartResumesSamePendingRequest() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var pendingStore = PendingStore(root);
            HostedProvisioningState pending = HostedProvisioningState.Create(Origin, [Origin]);
            await pendingStore.SavePendingAsync(pending);
            string? requestBody = null;
            var handler = new Handler(async (request, cancellationToken) =>
            {
                requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Ack();
            });
            await using var connection = Connection(ActiveStore(Path.Combine(root, "active.private")), pendingStore, handler);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Assert.True(connection.CanCopy);
            Assert.Contains(pending.RequestId, requestBody);
            Assert.Null(await pendingStore.LoadPendingAsync());
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task AcknowledgedRestartPromotesWithoutAnotherCreateRequest() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var pendingStore = PendingStore(root);
            await pendingStore.SavePendingAsync(HostedProvisioningState.Create(Origin, [Origin]).Acknowledged(OverlayId));
            int requests = 0;
            var handler = new Handler((_, _) => { requests++; throw new InvalidOperationException("must not create"); });
            var active = ActiveStore(Path.Combine(root, "active.private"));
            await using var connection = Connection(active, pendingStore, handler);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Assert.Equal(0, requests);
            Assert.True(connection.CanCopy);
            Assert.Equal(OverlayId, (await active.LoadAsync())!.OverlayId);
            Assert.Null(await pendingStore.LoadPendingAsync());
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task SenderConstructionFailureStaysNonCopyableAndRetriesAcknowledgedPendingExactly() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var pendingStore = PendingStore(root);
            await pendingStore.SavePendingAsync(HostedProvisioningState.Create(Origin, [Origin]).Acknowledged(OverlayId));
            int constructions = 0, requests = 0;
            var active = ActiveStore(Path.Combine(root, "active.private"));
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, active, config =>
            {
                if (Interlocked.Increment(ref constructions) == 1) throw new InvalidOperationException("synthetic construction failure");
                return new HostedOverlayPublisher(config, new Server());
            }, pendingStore, new HostedOverlayProvisioningClient(Origin,
                new Handler((_, _) => { requests++; throw new InvalidOperationException("must not create"); })), (_, _) => Task.CompletedTask);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Assert.False(connection.CanCopy);
            Assert.True(connection.CanRetry);
            Assert.NotNull(await pendingStore.LoadPendingAsync());
            await connection.RetryAsync();
            Assert.Equal(0, requests);
            Assert.Equal(2, constructions);
            Assert.True(connection.CanCopy);
            Assert.False(connection.CanRetry);
            Assert.Null(await pendingStore.LoadPendingAsync());
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task SenderAttachmentFailureStaysNonCopyableAndRetriesAcknowledgedPendingExactly() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var pendingStore = PendingStore(root);
            await pendingStore.SavePendingAsync(HostedProvisioningState.Create(Origin, [Origin]).Acknowledged(OverlayId));
            int attachments = 0, requests = 0;
            var active = ActiveStore(Path.Combine(root, "active.private"));
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, active,
                config => new HostedOverlayPublisher(config, new Server()), pendingStore,
                new HostedOverlayProvisioningClient(Origin,
                    new Handler((_, _) => { requests++; throw new InvalidOperationException("must not create"); })),
                (_, _) => Task.CompletedTask, (adapter, sender) =>
                {
                    if (Interlocked.Increment(ref attachments) == 1) throw new InvalidOperationException("synthetic attachment failure");
                    adapter.Attach(sender);
                });
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Assert.False(connection.CanCopy);
            Assert.True(connection.CanRetry);
            Assert.NotNull(await pendingStore.LoadPendingAsync());
            await connection.RetryAsync();
            Assert.Equal(0, requests);
            Assert.Equal(2, attachments);
            Assert.True(connection.CanCopy);
            Assert.False(connection.CanRetry);
            Assert.Null(await pendingStore.LoadPendingAsync());
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task ActiveSaveFailureRetainsAcknowledgedPendingForExactLocalRetry() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var pendingStore = PendingStore(root);
            await pendingStore.SavePendingAsync(HostedProvisioningState.Create(Origin, [Origin]).Acknowledged(OverlayId));
            var protector = new FailingProtector { Fail = true };
            var active = new HostedPublisherConfigurationStore(Path.Combine(root, "active.private"), protector, [Origin]);
            int requests = 0;
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, active,
                config => new HostedOverlayPublisher(config, new Server()), pendingStore,
                new HostedOverlayProvisioningClient(Origin,
                    new Handler((_, _) => { requests++; throw new InvalidOperationException("must not create"); })),
                (_, _) => Task.CompletedTask);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Assert.False(connection.CanCopy);
            Assert.True(connection.CanRetry);
            Assert.NotNull(await pendingStore.LoadPendingAsync());
            protector.Fail = false;
            await connection.RetryAsync();
            Assert.Equal(0, requests);
            Assert.True(connection.CanCopy);
            Assert.Null(await pendingStore.LoadPendingAsync());
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task PendingDeleteFailureKeepsHealthyActiveAndRestartCleansMatchingPending() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            string pendingPath = Path.Combine(root, "pending.private");
            var pendingStore = PendingStore(root);
            await pendingStore.SavePendingAsync(HostedProvisioningState.Create(Origin, [Origin]).Acknowledged(OverlayId));
            var active = ActiveStore(Path.Combine(root, "active.private"));
            FileStream? deletionLock = null;
            await using (var first = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, active,
                config => new HostedOverlayPublisher(config, new Server()), pendingStore,
                new HostedOverlayProvisioningClient(Origin,
                    new Handler((_, _) => throw new InvalidOperationException("must not create"))),
                (_, _) => Task.CompletedTask, (adapter, sender) =>
                {
                    adapter.Attach(sender);
                    deletionLock = new FileStream(pendingPath, FileMode.Open, FileAccess.Read, FileShare.None);
                }))
            {
                await first.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
                Assert.True(first.CanCopy);
                Assert.False(first.CanRetry);
                Assert.Equal(string.Empty, first.StatusText);
                Assert.True(File.Exists(pendingPath));
                deletionLock!.Dispose(); deletionLock = null;
            }

            int requests = 0;
            await using var restarted = Connection(active, pendingStore,
                new Handler((_, _) => { requests++; throw new InvalidOperationException("must not create"); }));
            await restarted.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Assert.Equal(0, requests);
            Assert.True(restarted.CanCopy);
            Assert.Null(await pendingStore.LoadPendingAsync());
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task ExistingVersionOneConfigurationWinsWithoutProvisioning() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var active = ActiveStore(Path.Combine(root, "active.private"));
            HostedPublisherConfiguration legacy = HostedPublisherConfiguration.Create(Origin,
                new string('a', 32), new string('b', 64), new string('c', 64), [Origin]);
            await active.SaveAsync(legacy);
            int requests = 0;
            var handler = new Handler((_, _) => { Interlocked.Increment(ref requests); return Task.FromResult(Ack()); });
            await using var connection = Connection(active, PendingStore(root), handler);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            await connection.EnsureProvisionedAsync();
            Assert.Equal(0, requests);
            string? copied = null;
            Assert.True(connection.CopyReadUrl(value => copied = value));
            Assert.Equal(legacy.BuildReadUrl(), copied);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task ClaimingPreReleaseStateUsesPublisherProbeAndPromotesLocally() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            string id = new('a', 32), read = new('b', 64), write = new('c', 64);
            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                origin = Origin,
                slotId = id,
                setupGrant = new string('d', 64),
                requestId = new string('e', 32),
                readCapability = read,
                writeCapability = write,
                readVerifier = HostedPublisherConfiguration.Verifier(id, "read", read),
                writeVerifier = HostedPublisherConfiguration.Verifier(id, "write", write),
                phase = "claiming",
                paused = false
            });
            var pending = PendingStore(root);
            await pending.SavePendingAsync(HostedProvisioningState.Decode(encoded, [Origin]));
            int requests = 0;
            var handler = new Handler((request, _) =>
            {
                Interlocked.Increment(ref requests);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal($"{Origin}/api/v1/overlays/{id}/publisher", request.RequestUri!.AbsoluteUri);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal(write, request.Headers.Authorization?.Parameter);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });
            var active = ActiveStore(Path.Combine(root, "active.private"));
            await using var connection = Connection(active, pending, handler);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Assert.Equal(1, requests);
            Assert.Equal(id, (await active.LoadAsync())!.OverlayId);
            Assert.Null(await pending.LoadPendingAsync());
            Assert.True(connection.CanCopy);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task BoundedFailureRetainsPendingAndExposesRetry() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            int requests = 0;
            var handler = new Handler((_, _) => { Interlocked.Increment(ref requests); throw new HttpRequestException("private detail"); });
            var pending = PendingStore(root);
            await using var connection = Connection(ActiveStore(Path.Combine(root, "active.private")), pending, handler);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            await connection.EnsureProvisionedAsync();
            Assert.Equal(3, requests);
            Assert.True(connection.CanRetry);
            Assert.False(connection.CanCopy);
            Assert.NotNull(await pending.LoadPendingAsync());
            Assert.DoesNotContain("private detail", connection.StatusText);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task ShutdownCancellationRetainsTheExactPendingRequest() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Handler(async (_, cancellationToken) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            });
            var active = ActiveStore(Path.Combine(root, "active.private"));
            var pending = PendingStore(root);
            var connection = Connection(active, pending, handler);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Task provisioning = connection.EnsureProvisionedAsync();
            await entered.Task;
            await connection.DisposeAsync();
            await provisioning;
            Assert.NotNull(await pending.LoadPendingAsync());
            Assert.Null(await active.LoadAsync());
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task ActiveVersionOneWithStalePendingNeverCreatesOrReplacesEitherState() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var active = ActiveStore(Path.Combine(root, "active.private"));
            HostedPublisherConfiguration legacy = HostedPublisherConfiguration.Create(Origin,
                new string('a', 32), new string('b', 64), new string('c', 64), [Origin]);
            await active.SaveAsync(legacy);
            var pending = PendingStore(root);
            await pending.SavePendingAsync(HostedProvisioningState.Create(Origin, [Origin]));
            int requests = 0;
            await using var connection = Connection(active, pending,
                new Handler((_, _) => { requests++; throw new InvalidOperationException("must not create"); }));
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            await connection.EnsureProvisionedAsync();
            Assert.Equal(0, requests);
            Assert.Equal(legacy.BuildReadUrl(), connection.UrlText);
            Assert.NotNull(await pending.LoadPendingAsync());
            Assert.Equal(legacy.BuildReadUrl(), (await active.LoadAsync())!.BuildReadUrl());
        }
        finally { Directory.Delete(root, true); }
    });

    private static HostedOverlayConnection Connection(HostedPublisherConfigurationStore active,
        HostedProvisioningStateStore pending, HttpMessageHandler handler) =>
        new(Dispatcher.CurrentDispatcher, active, config => new HostedOverlayPublisher(config, new Server()), pending,
            new HostedOverlayProvisioningClient(Origin, handler), (_, _) => Task.CompletedTask);
    private static HostedPublisherConfigurationStore ActiveStore(string path) => new(path, new CurrentUserDpapiSecretProtector(), [Origin]);
    private static HostedProvisioningStateStore PendingStore(string root) => new(Path.Combine(root, "pending.private"), new CurrentUserDpapiSecretProtector(), [Origin]);
    private static string NewRoot() { string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static HttpResponseMessage Ack() => new(HttpStatusCode.OK)
    { Content = new StringContent($"{{\"v\":1,\"status\":\"provisioned\",\"overlayId\":\"{OverlayId}\"}}", Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class FailingProtector : IStateSecretProtector
    {
        public bool Fail { get; set; }
        public byte[] Protect(byte[] plaintext) => Fail ? throw new InvalidOperationException("synthetic save failure") : [.. plaintext];
        public byte[] Unprotect(byte[] ciphertext) => [.. ciphertext];
    }
}
