using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class HostedDesktopPublisherTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(7L)]
    [InlineData(long.MaxValue)]
    public async Task ManualStartupPublishesPersistedValueWithoutAReader(long value)
    {
        var server = new Server();
        await using var sender = new HostedOverlayPublisher(Configuration(), server);
        var state = new PersistentTrackerState(1, GameId.DemonsSouls, OverlayConfiguration.Default,
            manualDemonsSoulsDeathCounter: ManualDeathCounter.CreateFor(GameId.DemonsSouls, value));
        await using var adapter = new HostedDesktopPublisher(sender, state);
        await WaitUntil(() => server.Value == value.ToString(System.Globalization.CultureInfo.InvariantCulture) && sender.Status == HostedPublisherStatus.Ready);
        Assert.Equal("available", server.Availability);
        adapter.StopOffering();
        await adapter.PublishAsync(new(state, TrackerCommandType.IncrementManualDeaths));
        await adapter.DisposeAsync();
        Assert.Single(server.Writes);
    }

    [Theory]
    [InlineData("ds1")]
    [InlineData("ds2")]
    [InlineData("ds3")]
    [InlineData("sekiro")]
    [InlineData("bloodborne")]
    [InlineData("elden_ring")]
    [InlineData("black_myth_wukong")]
    [InlineData("lies_of_p")]
    public async Task RealAcceptedSessionHoldsStartupAndPublishesZeroAndConfirmedLower(string game)
    {
        var state = RuntimePublicationSessionTests.Selected(GameId.Parse(game));
        var server = new Server();
        await using var sender = new HostedOverlayPublisher(Configuration(), server);
        await using var adapter = new HostedDesktopPublisher(sender, state);
        var session = new RuntimePublicationSession();
        session.SelectState(state);
        int second = 0;
        RuntimeGameReadResult Read(long value) => RuntimeGameReadResult.Synced(new RuntimeGameObservation(state.SelectedGameId,
            value, DateTimeOffset.UnixEpoch.AddSeconds(++second), EffectiveDeathTotalResult.SourceIdentityFor(state)));
        void Deliver(RuntimeGameReadResult? raw) => session.CompleteRead(session.BeginRead(state), state, raw, _ => { }, r => adapter.PublishAccepted(state, r));
        await WaitUntil(() => sender.Status == HostedPublisherStatus.Ready);
        Deliver(null);
        Deliver(RuntimeGameReadResult.WaitingForSaveFile(state.SelectedGameId));
        Deliver(RuntimeGameReadResult.SelectedSaveUnreadable(state.SelectedGameId));
        Deliver(RuntimeGameReadResult.Unavailable(state.SelectedGameId));
        Deliver(RuntimeGameReadResult.Cached(Read(40)));
        await adapter.PublishAsync(new(state, TrackerCommandType.SelectGame));
        Assert.All(server.Writes, json => Assert.False(JsonDocument.Parse(json).RootElement.TryGetProperty("death", out _)));
        Deliver(Read(0));
        await WaitUntil(() => server.Value == "0" && sender.Status == HostedPublisherStatus.Ready);
        Assert.Equal("available", server.Availability);
        Deliver(Read(100));
        await WaitUntil(() => server.Value == "100" && sender.Status == HostedPublisherStatus.Ready);
        int before = server.Writes.Count;
        var lower = Read(90);
        Deliver(lower);
        Deliver(RuntimeGameReadResult.Cached(lower));
        Deliver(RuntimeGameReadResult.Synced(new RuntimeGameObservation(state.SelectedGameId, 999, DateTimeOffset.UtcNow, "wrong-source")));
        Assert.Equal(before, server.Writes.Count);
        Deliver(Read(90));
        await WaitUntil(() => server.Value == "90" && sender.Status == HostedPublisherStatus.Ready);
        Deliver(null);
        await WaitUntil(() => server.Availability == "unavailable" && sender.Status == HostedPublisherStatus.Ready);
        Assert.Equal(game is "black_myth_wukong" or "lies_of_p" ? null : "0", server.Value);
        adapter.StopOffering();
        await adapter.DisposeAsync();
        Assert.All(server.Writes, body =>
        {
            Assert.DoesNotContain("source", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:/", body);
            Assert.DoesNotContain("observedAt", body);
            Assert.DoesNotContain("adjustment", body);
            Assert.DoesNotContain("gameId", body);
        });
    }

    internal static HostedPublisherConfiguration Configuration() => HostedPublisherConfiguration.Create(
        "https://publisher.example.test", new('a', 32), new('b', 64), new('c', 64), ["https://publisher.example.test"]);

    internal sealed class Server : HttpMessageHandler
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Writes = new();
        internal string? Value;
        internal string? Availability;
        private string deathDigest = HostedPublisherProtocol.Digest(null);
        private string appearanceDigest = new('0', 64);
        private long deathRevision, appearanceRevision;
        internal Func<CancellationToken, Task>? BeforeSend { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (BeforeSend is not null) await BeforeSend(cancellationToken);
            object Channel(string digest, long revision) => new { revision = revision.ToString(System.Globalization.CultureInfo.InvariantCulture), digest };
            HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            if (request.Method == HttpMethod.Get) return Reply(new { v = 1, epoch = "0", generation = "0", death = Channel(deathDigest, deathRevision), appearance = Channel(appearanceDigest, appearanceRevision) });
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            string session = root.GetProperty("sessionRequestId").GetString()!;
            if (request.Method == HttpMethod.Post) return Reply(new { v = 1, epoch = "1", generation = "0", sessionRequestId = session, death = Channel(deathDigest, deathRevision), appearance = Channel(appearanceDigest, appearanceRevision) });
            string previousDeath = deathDigest, previousAppearance = appearanceDigest;
            if (root.TryGetProperty("death", out var d))
            {
                var death = HostedOverlayJson.Parse("{\"v\":1,\"type\":\"update\",\"death\":{\"revision\":\"0\"," + d.GetRawText()[1..] + "}").Death;
                deathDigest = HostedPublisherProtocol.Digest(death);
                Value = death!.Value;
                Availability = death.Availability;
            }
            if (root.TryGetProperty("appearance", out var a))
                appearanceDigest = HostedPublisherProtocol.Digest(HostedOverlayJson.Parse("{\"v\":1,\"type\":\"update\",\"appearance\":{\"revision\":\"0\"," + a.GetRawText()[1..] + "}").Appearance);
            Writes.Enqueue(body);
            List<string> changed = [];
            if (previousDeath != deathDigest) { deathRevision++; changed.Add("death"); }
            if (previousAppearance != appearanceDigest) { appearanceRevision++; changed.Add("appearance"); }
            return Reply(new { v = 1, epoch = "1", generation = "0", sessionRequestId = session, sequence = root.GetProperty("sequence").GetString(), changed, death = Channel(deathDigest, deathRevision), appearance = Channel(appearanceDigest, appearanceRevision) });
        }
    }
    internal static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
}
