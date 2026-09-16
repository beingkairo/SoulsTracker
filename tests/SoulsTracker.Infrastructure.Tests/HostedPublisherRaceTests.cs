using System.Net;
using System.Text.Json;
using SoulsTracker.Application;
using static SoulsTracker.Infrastructure.Tests.HostedPublisherTests;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HostedPublisherRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOfferDuringInitializationStopsAcquisitionAndRetries(bool transient)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new Server
        {
            Intercept = async (_, _, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return transient ? new(HttpStatusCode.ServiceUnavailable) : null;
        }
        };
        int delays = 0;
        await using var sender = new HostedOverlayPublisher(Configuration(), server, delay: (_, ct) =>
        {
            Interlocked.Increment(ref delays);
            return Task.Delay(10, ct);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(sender.Offer(Offer("invalid")));
        release.SetResult();
        await sender.DisposeAsync();
        Assert.Single(server.Methods);
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task QueuedOfferImmediatelyLeavesReadyStatus()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new Server
        {
            Intercept = async (request, _, ct) =>
        {
            if (request.Method == HttpMethod.Put) await blocked.Task.WaitAsync(ct);
            return null;
        }
        };
        await using var sender = new HostedOverlayPublisher(Configuration(), server);
        await WaitUntil(() => sender.Status == HostedPublisherStatus.Ready);
        Assert.True(sender.Offer(Offer("1")));
        Assert.NotEqual(HostedPublisherStatus.Ready, sender.Status);
        blocked.SetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewDeathDuringBlockedAppearancePreservesBothDirtyChannels(bool loseResponse)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new Server();
        int puts = 0;
        server.Intercept = async (request, _, ct) =>
        {
            if (request.Method == HttpMethod.Put && Interlocked.Increment(ref puts) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
                if (loseResponse) throw new HttpRequestException("synthetic private diagnostic");
            }
            return null;
        };
        await using var sender = new HostedOverlayPublisher(Configuration(), server, delay: (_, ct) => Task.Delay(1, ct));
        var original = Offer("1");
        sender.Offer(original);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sender.Offer(new("update", true, original.Death! with { Value = "2" }));
        sender.Offer(new("update", false, null, original.Appearance! with { Title = "Newest" }));
        sender.Offer(new("update", true, original.Death! with { Value = "3" }));
        Assert.Equal(1, Volatile.Read(ref puts));
        release.SetResult();
        await WaitUntil(() => sender.Status == HostedPublisherStatus.Ready && Volatile.Read(ref puts) == 2);
        await sender.DisposeAsync();
        using var final = JsonDocument.Parse(server.Bodies.Last());
        Assert.Equal("2", final.RootElement.GetProperty("sequence").GetString());
        Assert.Equal("3", final.RootElement.GetProperty("death").GetProperty("value").GetString());
        Assert.Equal("Newest", final.RootElement.GetProperty("appearance").GetProperty("title").GetString());
        Assert.Equal(1, server.MaximumConcurrent);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("state")]
    public async Task LostResponseRetriesIdenticalBodyAndSuppressesAcknowledgedNoOps(string action)
    {
        var server = new Server();
        int attempts = 0;
        server.AfterCommit = (committedAction, response) =>
        {
            if (committedAction == action && ++attempts == 1)
            {
                response.Dispose();
                throw new HttpRequestException("lost response");
            }
            return response;
        };
        await using var sender = new HostedOverlayPublisher(Configuration(), server, random: () => 0, delay: (_, _) => Task.CompletedTask);
        sender.Offer(Offer("1"));
        await WaitUntil(() => sender.Status == HostedPublisherStatus.Ready);
        sender.Offer(Offer("1"));
        await sender.DisposeAsync();
        string method = action == "session" ? "POST" : "PUT";
        var bodies = server.Bodies.Where((_, i) => server.Methods[i] == method).ToArray();
        Assert.Equal(2, bodies.Length);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.Equal(1, server.MaximumConcurrent);
        Assert.Equal(1, server.Methods.Count(m => m == "GET"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostCommittedWritePreservesRevertedOrNoOpChannelsWhenSuperseded(bool revertDeath)
    {
        var server = new Server { DeathDigest = HostedPublisherProtocol.Digest(Offer("1").Death) };
        var retrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int puts = 0;
        server.AfterCommit = (action, response) =>
        {
            if (action == "state" && ++puts == 1)
            {
                response.Dispose();
                throw new HttpRequestException();
            }
            return response;
        };
        await using var sender = new HostedOverlayPublisher(Configuration(), server, delay: async (_, ct) =>
        {
            retrying.SetResult();
            await release.Task.WaitAsync(ct);
        });
        sender.Offer(Offer("2"));
        await retrying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (revertDeath) sender.Offer(new("update", true, Offer("1").Death));
        else sender.Offer(new("update", false, null, Offer("2").Appearance! with { Title = "New style" }));
        release.SetResult();
        await WaitUntil(() => sender.Status == HostedPublisherStatus.Ready);
        await sender.DisposeAsync();
        using var final = JsonDocument.Parse(server.Bodies.Last());
        Assert.Equal(revertDeath ? "1" : "2", final.RootElement.GetProperty("death").GetProperty("value").GetString());
        Assert.True(final.RootElement.TryGetProperty("appearance", out _));
        Assert.Equal("2", final.RootElement.GetProperty("sequence").GetString());
    }

    [Theory]
    [InlineData("session")]
    [InlineData("state")]
    public async Task ConflictAfterStartupNeverRereadsOrReacquires(string action)
    {
        var server = new Server
        {
            Intercept = (request, _, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath.EndsWith(action, StringComparison.Ordinal) ? new(HttpStatusCode.Conflict) : null)
        };
        await using var sender = new HostedOverlayPublisher(Configuration(), server);
        sender.Offer(Offer("1"));
        await WaitUntil(() => sender.Status == HostedPublisherStatus.Conflict);
        Assert.False(sender.Offer(Offer("2")));
        await sender.DisposeAsync();
        Assert.Equal(1, server.Methods.Count(m => m == "GET"));
        Assert.Equal(1, server.Methods.Count(m => m == "POST"));
        Assert.Equal(action == "state" ? 1 : 0, server.Methods.Count(m => m == "PUT"));
    }

    [Theory]
    [InlineData(401, HostedPublisherStatus.CredentialsRequired)]
    [InlineData(403, HostedPublisherStatus.CredentialsRequired)]
    [InlineData(409, HostedPublisherStatus.Conflict)]
    [InlineData(400, HostedPublisherStatus.InvalidProtocol)]
    [InlineData(413, HostedPublisherStatus.InvalidProtocol)]
    [InlineData(302, HostedPublisherStatus.InvalidProtocol)]
    [InlineData(404, HostedPublisherStatus.InvalidProtocol)]
    public async Task PermanentFailuresPauseWithoutReacquisitionOrSecretDiagnostics(int code, HostedPublisherStatus expected)
    {
        var server = new Server
        {
            Intercept = (_, _, _) => Task.FromResult<HttpResponseMessage?>(new((HttpStatusCode)code)
            { Content = new StringContent(new string('c', 64)) })
        };
        await using var sender = new HostedOverlayPublisher(Configuration(), server);
        sender.Offer(Offer("1"));
        await WaitUntil(() => sender.Status == expected);
        Assert.False(sender.Offer(Offer("2")));
        Assert.DoesNotContain(new string('c', 64), sender.Status.ToString());
        await sender.DisposeAsync();
        Assert.Single(server.Methods);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task TransientStatusHonorsBoundedRetryAfter(int code)
    {
        var delays = new List<TimeSpan>();
        var server = new Server();
        int calls = 0;
        server.Intercept = (_, _, _) =>
        {
            if (++calls != 1) return Task.FromResult<HttpResponseMessage?>(null);
            var response = new HttpResponseMessage((HttpStatusCode)code);
            response.Headers.RetryAfter = new(TimeSpan.FromHours(1));
            return Task.FromResult<HttpResponseMessage?>(response);
        };
        await using var sender = new HostedOverlayPublisher(Configuration(), server, random: () => 0.5,
            delay: (time, _) => { delays.Add(time); return Task.CompletedTask; });
        sender.Offer(Offer("1"));
        await WaitUntil(() => sender.Status == HostedPublisherStatus.Ready);
        await sender.DisposeAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(delays));
        Assert.Equal(1, server.MaximumConcurrent);
    }

    [Fact]
    public async Task AcknowledgementMismatchOrOversizeNeverClearsPendingState()
    {
        foreach (string body in new[] { "{}", new string('x', 8193), "{\"v\":1,\"v\":1}" })
        {
            var server = new Server
            {
                Intercept = (request, _, _) => Task.FromResult<HttpResponseMessage?>(
                request.Method == HttpMethod.Put ? new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") } : null)
            };
            await using var sender = new HostedOverlayPublisher(Configuration(), server);
            sender.Offer(Offer("1"));
            await WaitUntil(() => sender.Status == HostedPublisherStatus.InvalidProtocol);
            await sender.DisposeAsync();
            Assert.Equal(1, server.Methods.Count(m => m == "PUT"));
        }
    }

    [Fact]
    public async Task ShutdownCancelsBlockedSendRejectsLateOffersAndDisposesOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new Server
        {
            Intercept = async (_, _, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { exited.SetResult(); }
            return null;
        }
        };
        var sender = new HostedOverlayPublisher(Configuration(), server);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task first = sender.DisposeAsync().AsTask();
        Assert.False(sender.Offer(Offer("1")));
        Assert.Same(first, sender.DisposeAsync().AsTask());
        await first.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(exited.Task.IsCompletedSuccessfully);
        Assert.Equal(HostedPublisherStatus.Stopped, sender.Status);
        Assert.Single(server.Methods);
    }

    [Fact]
    public void OwnedHandlerDeniesRedirectsAndCookies()
    {
        using var handler = HostedOverlayPublisher.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
}
