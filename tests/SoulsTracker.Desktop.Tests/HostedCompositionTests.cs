using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.HostedDesktopPublisherTests;
using static SoulsTracker.Desktop.Tests.HostedConnectionTests;

namespace SoulsTracker.Desktop.Tests;

public sealed class HostedCompositionTests
{
    [Theory]
    [InlineData("ds1")]
    [InlineData("ds2")]
    [InlineData("ds3")]
    [InlineData("sekiro")]
    [InlineData("bloodborne")]
    [InlineData("elden_ring")]
    [InlineData("black_myth_wukong")]
    [InlineData("lies_of_p")]
    public async Task ComposedAcceptedBoundaryPreservesStartupHoldLowerAndSourceSwitch(string game) => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = RuntimePublicationSessionTests.Selected(GameId.Parse(game));
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            var server = new Server();
            HostedOverlayPublisher? sender = null;
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, config => sender = new HostedOverlayPublisher(config, server));
            var session = new RuntimePublicationSession(); session.SelectState(state);
            var repository = new SqliteTrackerStateRepository(root, "tracker.db"); await repository.SaveAsync(state);
            await using var text = new TextExportStatePublisher();
            await using var coordinator = new SerializedTrackerCoordinator(repository,
                new DesktopStateChangePublisher(Dispatcher.CurrentDispatcher, session, new CompositeTrackerStateChangePublisher(connection, text)));
            await coordinator.InitializeAsync();
            await connection.InitializeAsync(state);
            state = await coordinator.SetTextExportConfigurationAsync(new(Path.Combine(root, "deaths.txt"), true));
            int tick = 0;
            RuntimeGameReadResult Read(long value) => RuntimeGameReadResult.Synced(new RuntimeGameObservation(state.SelectedGameId,
                value, DateTimeOffset.UnixEpoch.AddSeconds(++tick), EffectiveDeathTotalResult.SourceIdentityFor(state)));
            void Deliver(RuntimeGameReadResult? value) => session.CompleteRead(session.BeginRead(state), state, value, _ => { }, accepted =>
            {
                connection.PublishAccepted(session.CurrentState!, accepted);
                text.PublishRuntimeObservation(session.CurrentState!, accepted);
            });
            Deliver(null); Deliver(RuntimeGameReadResult.WaitingForSaveFile(state.SelectedGameId));
            Deliver(RuntimeGameReadResult.SelectedSaveUnreadable(state.SelectedGameId));
            Deliver(RuntimeGameReadResult.Cached(Read(99)));
            state = (await coordinator.SubmitAsync(new UpdateOverlayPresentationCommand(true, false))).CommittedState!;
            await WaitUntil(() => sender!.Status == HostedPublisherStatus.Ready);
            Assert.All(server.Writes, body => Assert.False(JsonDocument.Parse(body).RootElement.TryGetProperty("death", out _)));
            Deliver(Read(0));
            await WaitUntil(() => server.Value == "0" && sender!.Status == HostedPublisherStatus.Ready);
            Deliver(Read(100));
            await WaitUntil(() => server.Value == "100" && sender!.Status == HostedPublisherStatus.Ready);
            int before = server.Writes.Count;
            var lower = Read(90); Deliver(lower); Deliver(RuntimeGameReadResult.Cached(lower));
            Assert.Equal(before, server.Writes.Count);
            state = (await coordinator.SubmitAsync(new UpdateOverlayPresentationCommand(false, false))).CommittedState!;
            await WaitUntil(() => sender!.Status == HostedPublisherStatus.Ready);
            Assert.Equal("100", server.Value);
            Deliver(Read(90));
            await WaitUntil(() => server.Value == "90" && sender!.Status == HostedPublisherStatus.Ready);
            var old = session.BeginRead(state); var oldState = state; var stale = Read(900);
            state = (await coordinator.SubmitAsync(new SelectGameCommand(GameId.DemonsSouls))).CommittedState!;
            await WaitUntil(() => server.Value == "0" && sender!.Status == HostedPublisherStatus.Ready);
            state = (await coordinator.SubmitAsync(new SelectGameCommand(oldState.SelectedGameId))).CommittedState!;
            await WaitUntil(() => server.Availability == "unavailable" && sender!.Status == HostedPublisherStatus.Ready);
            Assert.Equal(game is "black_myth_wukong" or "lies_of_p" ? null : "0", server.Value);
            session.CompleteRead(old, oldState, stale, _ => Assert.Fail("stale UI"), _ => Assert.Fail("stale output"));
            connection.StopSetup();
            await coordinator.DisposeAsync();
            await Task.WhenAll(connection.DisposeAsync().AsTask(), text.DisposeAsync().AsTask());
            Assert.Equal(HostedPublisherStatus.Stopped, sender!.Status);
            Assert.True(File.Exists(state.TextExports.DeathsPath));
            Assert.All(server.Writes, body => { Assert.DoesNotContain("sourceIdentity", body); Assert.DoesNotContain(root, body); });
        }
        finally { Directory.Delete(root, true); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComposedBlockedSenderDoesNotBlockRealManualSqliteAndTxt(bool failing) => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new HostedPublisherConfigurationStore(Path.Combine(root, "pairing.private"), new CurrentUserDpapiSecretProtector(), [Configuration().DisplayOrigin]);
            await store.SaveAsync(Configuration());
            var server = new Server { BeforeSend = async ct => { if (failing) throw new HttpRequestException(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); } };
            HostedOverlayPublisher? sender = null;
            await using var connection = new HostedOverlayConnection(Dispatcher.CurrentDispatcher, store, config => sender = new HostedOverlayPublisher(config, server));
            var state = new PersistentTrackerState(1, GameId.DemonsSouls, OverlayConfiguration.Default, textExports: new(Path.Combine(root, "deaths.txt"), true));
            var repository = new SqliteTrackerStateRepository(root, "tracker.db"); await repository.SaveAsync(state);
            var session = new RuntimePublicationSession(); session.SelectState(state);
            await using var text = new TextExportStatePublisher();
            await using var coordinator = new SerializedTrackerCoordinator(repository,
                new DesktopStateChangePublisher(Dispatcher.CurrentDispatcher, session, new CompositeTrackerStateChangePublisher(connection, text)));
            await coordinator.InitializeAsync(); await connection.InitializeAsync(state);
            await coordinator.SubmitAsync(new IncrementManualDeathsCommand()).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, (await repository.LoadAsync()).State!.ManualDemonsSoulsDeathCounter.Value);
            connection.StopSetup();
            await coordinator.DisposeAsync();
            await text.DisposeAsync();
            Assert.Equal("Total Deaths: 1", await File.ReadAllTextAsync(state.TextExports.DeathsPath!));
            await connection.DisposeAsync();
            Assert.Empty(server.Writes);
            Assert.Equal(HostedPublisherStatus.Stopped, sender!.Status);
        }
        finally { Directory.Delete(root, true); }
    });
}
