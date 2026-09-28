using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
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
    private const string Code = "st1.11111111111111111111111111111111.2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public async Task SetupProtectsPendingBeforeNetworkPromotesAndCopiesReadOnlyUrl() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            string activePath = Path.Combine(root, "active.private"), pendingPath = Path.Combine(root, "pending.private");
            bool protectedBeforeSend = false;
            var handler = new Handler(async (request, cancellationToken) =>
            {
                protectedBeforeSend = File.Exists(pendingPath) && !Encoding.UTF8.GetString(await File.ReadAllBytesAsync(pendingPath, cancellationToken)).Contains(Code, StringComparison.Ordinal);
                return Ack();
            });
            var active = new HostedPublisherConfigurationStore(activePath, new CurrentUserDpapiSecretProtector(), [Origin]);
            var pending = new HostedProvisioningStateStore(pendingPath, new CurrentUserDpapiSecretProtector(), [Origin]);
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, active,
                config => new HostedOverlayPublisher(config, new Server()), pending,
                new HostedOverlayProvisioningClient(Origin, handler), (_, _) => Task.CompletedTask);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Assert.False(connection.CanCopy);
            await connection.SetUpAsync(Code, consent: false, replacementConfirmed: true);
            Assert.False(connection.HasPendingSetup);
            await connection.SetUpAsync(Code, consent: true, replacementConfirmed: true);
            Assert.True(protectedBeforeSend);
            Assert.True(connection.CanCopy);
            Assert.False(connection.HasPendingSetup);
            Assert.NotNull(await active.LoadAsync());
            Assert.Null(await pending.LoadPendingAsync());
            string? copied = null;
            Assert.True(connection.CopyReadUrl(value => copied = value));
            Assert.StartsWith(Origin + "/overlay/#id=11111111111111111111111111111111&read=", copied, StringComparison.Ordinal);
            Assert.DoesNotContain("2222222222222222", copied);
            Assert.Equal("URL copied", connection.CopyFeedbackText);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task LostResponseRetriesTheExactProtectedRequest() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var requests = new List<(string Authorization, string Body)>();
            int attempt = 0;
            var handler = new Handler(async (request, cancellationToken) =>
            {
                requests.Add((request.Headers.Authorization!.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
                if (Interlocked.Increment(ref attempt) == 1) throw new HttpRequestException("lost response");
                return Ack();
            });
            await using var connection = Connection(root, handler, (_, _) => Task.CompletedTask);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            await connection.SetUpAsync(Code, consent: true, replacementConfirmed: true);
            Assert.Equal(2, requests.Count);
            Assert.Equal(requests[0], requests[1]);
            Assert.True(connection.CanCopy);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task DeniedReplacementRetainsActiveConnectionAndActiveRemovalKeepsPendingSetup() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var active = new HostedPublisherConfigurationStore(Path.Combine(root, "active.private"),
                new CurrentUserDpapiSecretProtector(), [Origin]);
            HostedPublisherConfiguration original = HostedPublisherConfiguration.Create(Origin,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('b', 64), new string('c', 64), [Origin]);
            await active.SaveAsync(original);
            int requests = 0;
            var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, active,
                config => new HostedOverlayPublisher(config, new Server()), PendingStore(root),
                new HostedOverlayProvisioningClient(Origin, new Handler((_, _) =>
                {
                    Interlocked.Increment(ref requests);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                })), (_, _) => Task.CompletedTask);
            await connection.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            await connection.SetUpAsync(Code, consent: true, replacementConfirmed: false);
            Assert.Equal(0, requests);
            await connection.SetUpAsync(Code, consent: true, replacementConfirmed: true);
            Assert.Equal(1, requests);
            Assert.True(connection.CanCopy);
            Assert.True(connection.IsSetupPaused);
            Assert.Equal(original.BuildReadUrl(), (await active.LoadAsync())!.BuildReadUrl());
            Assert.NotNull(await PendingStore(root).LoadPendingAsync());
            await connection.RemoveAsync(confirmed: true);
            Assert.Null(await active.LoadAsync());
            Assert.NotNull(await PendingStore(root).LoadPendingAsync());
            await connection.DisposeAsync();
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public async Task PauseRestartResumeAndConfirmedAbandonPreserveExactPendingState() => await OnDispatcher(async () =>
    {
        string root = NewRoot();
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocking = new Handler(async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Ack();
            });
            var first = Connection(root, blocking, (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
            await first.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Task setup = first.SetUpAsync(Code, consent: true, replacementConfirmed: true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await first.PauseSetupAsync();
            await setup;
            Assert.True(first.IsSetupPaused);
            var store = PendingStore(root);
            byte[] exact = (await store.LoadPendingAsync())!.Encode();
            await first.DisposeAsync();

            int resumedRequests = 0;
            var second = Connection(root, new Handler((_, _) =>
            {
                Interlocked.Increment(ref resumedRequests);
                return Task.FromResult(Ack());
            }), (_, _) => Task.CompletedTask);
            await second.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            Assert.Equal(0, resumedRequests);
            Assert.True(second.CanResumeSetup);
            Assert.Equal(exact, (await store.LoadPendingAsync())!.Encode());
            await second.AbandonSetupAsync(operatorResetConfirmed: false);
            Assert.NotNull(await store.LoadPendingAsync());
            await second.ResumeSetupAsync();
            Assert.Equal(1, resumedRequests);
            Assert.True(second.CanCopy);
            Assert.Null(await store.LoadPendingAsync());
            await second.DisposeAsync();

            var thirdPending = HostedProvisioningState.Create(Origin, Code, [Origin]).WithPaused(true);
            await store.SavePendingAsync(thirdPending);
            var third = Connection(root, new Handler((_, _) => Task.FromResult(Ack())), (_, _) => Task.CompletedTask);
            await third.InitializeAsync(RuntimePublicationSessionTests.Selected(GameId.DemonsSouls));
            await third.AbandonSetupAsync(operatorResetConfirmed: false);
            Assert.NotNull(await store.LoadPendingAsync());
            await third.AbandonSetupAsync(operatorResetConfirmed: true);
            Assert.Null(await store.LoadPendingAsync());
            Assert.True(third.CanCopy);
            await third.DisposeAsync();
        }
        finally { Directory.Delete(root, true); }
    });

    private static HostedOverlayConnection Connection(string root, HttpMessageHandler handler,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        var active = new HostedPublisherConfigurationStore(Path.Combine(root, "active.private"),
            new CurrentUserDpapiSecretProtector(), [Origin]);
        return new HostedOverlayConnection(Dispatcher.CurrentDispatcher, active,
            config => new HostedOverlayPublisher(config, new Server()), PendingStore(root),
            new HostedOverlayProvisioningClient(Origin, handler), delay);
    }

    private static HostedProvisioningStateStore PendingStore(string root) => new(Path.Combine(root, "pending.private"),
        new CurrentUserDpapiSecretProtector(), [Origin]);

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static HttpResponseMessage Ack() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"v\":1,\"status\":\"provisioned\"}", Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
