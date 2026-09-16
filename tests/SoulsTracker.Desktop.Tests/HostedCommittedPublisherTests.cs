using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using static SoulsTracker.Desktop.Tests.HostedDesktopPublisherTests;

namespace SoulsTracker.Desktop.Tests;

public sealed class HostedCommittedPublisherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedOrFailingNetworkCannotBlockManualCommandsSqliteOrActualTxt(bool fail) => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new Server
            {
                BeforeSend = async ct =>
            {
                entered.TrySetResult();
                if (fail) throw new HttpRequestException("synthetic");
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            };
            await using var sender = new HostedOverlayPublisher(Configuration(), server);
            var state = new PersistentTrackerState(1, GameId.DemonsSouls, OverlayConfiguration.Default,
                textExports: new TextExportConfiguration(Path.Combine(root, "deaths.txt"), true));
            await using var adapter = new HostedDesktopPublisher(sender, state);
            await using var text = new TextExportStatePublisher();
            var session = new RuntimePublicationSession();
            session.SelectState(state);
            var repository = new SqliteTrackerStateRepository(root, "tracker.db");
            await repository.SaveAsync(state);
            await using var coordinator = new SerializedTrackerCoordinator(repository,
                new DesktopStateChangePublisher(Dispatcher.CurrentDispatcher, session, new CompositeTrackerStateChangePublisher(adapter, text)));
            var desktop = new DesktopTrackerViewModel(coordinator);
            await desktop.InitializeAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var written = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            text.WriteCompleted += (_, success) => written.TrySetResult(success);
            await desktop.IncrementManualDeathsAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(await written.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("1", desktop.TotalDeathsText);
            Assert.Equal("Total Deaths: 1", await File.ReadAllTextAsync(state.TextExports.DeathsPath!));
            Assert.Equal(1, (await repository.LoadAsync()).State!.ManualDemonsSoulsDeathCounter.Value);
            await desktop.DecrementManualDeathsAsync();
            await desktop.DecrementManualDeathsAsync();
            Assert.Equal(0, (await repository.LoadAsync()).State!.ManualDemonsSoulsDeathCounter.Value);
            // Producers are drained before the hosted consumer's bounded flush/cancel.
            await coordinator.DisposeAsync();
            adapter.StopOffering();
            await text.DisposeAsync();
            await adapter.DisposeAsync();
            Assert.Empty(server.Writes);
            Assert.Equal("Total Deaths: 0", await File.ReadAllTextAsync(state.TextExports.DeathsPath!));
            Assert.Equal(HostedPublisherStatus.Stopped, sender.Status);
        }
        finally { Directory.Delete(root, true); }
    });

    [Theory]
    [InlineData("elden_ring")]
    [InlineData("black_myth_wukong")]
    [InlineData("lies_of_p")]
    public async Task CommittedAppearanceAdjustmentAndSourceSwitchUseRealSessionBoundary(string game) => await OnDispatcher(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = RuntimePublicationSessionTests.Selected(GameId.Parse(game));
            var server = new Server();
            await using var sender = new HostedOverlayPublisher(Configuration(), server);
            await using var adapter = new HostedDesktopPublisher(sender, state);
            var session = new RuntimePublicationSession();
            session.SelectState(state);
            var repository = new SqliteTrackerStateRepository(root, "tracker.db");
            await repository.SaveAsync(state);
            await using var text = new TextExportStatePublisher();
            await using var coordinator = new SerializedTrackerCoordinator(repository,
                new DesktopStateChangePublisher(Dispatcher.CurrentDispatcher, session, new CompositeTrackerStateChangePublisher(adapter, text)));
            await coordinator.InitializeAsync();
            state = await coordinator.SetTextExportConfigurationAsync(new(Path.Combine(root, "deaths.txt"), true));
            await WaitUntil(() => sender.Status == HostedPublisherStatus.Ready);
            state = (await coordinator.SubmitAsync(new UpdateOverlayPresentationCommand(false, false))).CommittedState!;
            await WaitUntil(() => server.Writes.Any(body => JsonDocument.Parse(body).RootElement.TryGetProperty("appearance", out var a) && !a.GetProperty("enabled").GetBoolean()));
            await WaitUntil(() => sender.Status == HostedPublisherStatus.Ready);
            Assert.All(server.Writes, body => Assert.False(JsonDocument.Parse(body).RootElement.TryGetProperty("death", out _)));
            int second = 0;
            RuntimeGameReadResult Read(long value) => RuntimeGameReadResult.Synced(new RuntimeGameObservation(state.SelectedGameId, value,
                DateTimeOffset.UnixEpoch.AddSeconds(++second), EffectiveDeathTotalResult.SourceIdentityFor(state)));
            void Deliver(RuntimeGameReadResult read) => session.CompleteRead(session.BeginRead(state), state, read, _ => { }, accepted =>
            {
                adapter.PublishAccepted(session.CurrentState!, accepted);
                text.PublishRuntimeObservation(session.CurrentState!, accepted);
            });
            Deliver(Read(100));
            await WaitUntil(() => server.Value == "100" && sender.Status == HostedPublisherStatus.Ready);
            Deliver(Read(90));
            int beforeAppearance = server.Writes.Count;
            state = (await coordinator.SubmitAsync(new UpdateOverlayPresentationCommand(true, false))).CommittedState!;
            await WaitUntil(() => server.Writes.Count > beforeAppearance && sender.Status == HostedPublisherStatus.Ready);
            using (var appearanceOnly = JsonDocument.Parse(server.Writes.Last()))
            {
                Assert.True(appearanceOnly.RootElement.TryGetProperty("appearance", out _));
                Assert.False(appearanceOnly.RootElement.TryGetProperty("death", out _));
            }
            Assert.Equal("100", server.Value);
            var delayed = session.BeginRead(state);
            var oldState = state;
            var oldRead = Read(88);
            int before = server.Writes.Count;
            if (game == "elden_ring")
            {
                state = (await coordinator.SubmitAsync(new AdjustEldenRingMissedDeathsCommand(true))).CommittedState!;
                await WaitUntil(() => server.Value == "101" && sender.Status == HostedPublisherStatus.Ready);
                Assert.Equal(before + 1, server.Writes.Count);
            }
            else state = await coordinator.SetTextExportConfigurationAsync(state.TextExports);
            Deliver(Read(90));
            string expected = game == "elden_ring" ? "91" : "90";
            await WaitUntil(() => server.Value == expected && sender.Status == HostedPublisherStatus.Ready);
            session.CompleteRead(delayed, oldState, oldRead, _ => Assert.Fail("stale desktop"), r => adapter.PublishAccepted(oldState, r));
            Assert.Equal(expected, server.Value);
            await coordinator.SubmitAsync(new SelectGameCommand(GameId.DemonsSouls));
            await WaitUntil(() => server.Value == "0" && server.Availability == "available" && sender.Status == HostedPublisherStatus.Ready);
            state = (await coordinator.SubmitAsync(new SelectGameCommand(oldState.SelectedGameId))).CommittedState!;
            await WaitUntil(() => server.Availability == "unavailable" && sender.Status == HostedPublisherStatus.Ready);
            Assert.Equal(game == "elden_ring" ? "0" : null, server.Value);
            session.CompleteRead(delayed, oldState, oldRead, _ => Assert.Fail("old generation"), _ => Assert.Fail("old output"));
            await coordinator.DisposeAsync();
            adapter.StopOffering();
            await text.DisposeAsync();
            await adapter.DisposeAsync();
            foreach (string json in server.Writes)
            {
                Assert.DoesNotContain(root, json);
                Assert.DoesNotContain("sourceIdentity", json);
                Assert.DoesNotContain("C:/saves", json);
            }
        }
        finally { Directory.Delete(root, true); }
    });

    private static async Task OnDispatcher(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
