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
            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, origin = Origin, slotId = id,
                setupGrant = new string('d', 64), requestId = new string('e', 32), readCapability = read,
                writeCapability = write, readVerifier = HostedPublisherConfiguration.Verifier(id, "read", read),
                writeVerifier = HostedPublisherConfiguration.Verifier(id, "write", write), phase = "claiming", paused = false });
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
}
