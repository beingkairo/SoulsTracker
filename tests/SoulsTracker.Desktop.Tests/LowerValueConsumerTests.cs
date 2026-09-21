using System.IO;
using System.Reflection;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;


namespace SoulsTracker.Desktop.Tests;

public sealed class LowerValueConsumerTests
{
    [Theory]
    [InlineData(90)]
    [InlineData(0)]
    public async Task PendingAndCachedCandidatesRetainConfirmedMetadataAndOutputs(int lowerValue)
    {
        string path = Path.Combine(Path.GetTempPath(), $"souls-lower-{Guid.NewGuid():N}.txt");
        try
        {
            PersistentTrackerState state = new(PersistentTrackerState.CurrentSchemaVersion,
                GameId.BlackMythWukong, OverlayConfiguration.Default,
                textExports: new TextExportConfiguration(path, true),
                blackMythWukongSave: new BlackMythWukongSaveConfiguration("C:/saves/test/ArchiveSaveFile.1.sav"));
            await using var coordinator = new SerializedTrackerCoordinator(new Repository(state), new NullPublisher());
            var desktop = new DesktopTrackerViewModel(coordinator);
            await desktop.InitializeAsync();
            await using var overlay = new HostedDesktopPublisher(null, state);
            await using var text = new TextExportStatePublisher();
            var session = new RuntimePublicationSession();
            TaskCompletionSource<bool> written = NewCompletion();
            int writes = 0;
            text.WriteCompleted += (_, success) => { Interlocked.Increment(ref writes); written.TrySetResult(success); };
            RuntimeGameReadResult Read(long value, int second, int level)
            {
                var observation = new RuntimeGameObservation(state.SelectedGameId, value, DateTimeOffset.UnixEpoch.AddSeconds(second),
                    EffectiveDeathTotalResult.SourceIdentityFor(state));
                var metadata = new BlackMythWukongSaveMetadata(level, TimeSpan.FromHours(level), DateTimeOffset.UnixEpoch.AddHours(level));
                return value == 0 ? RuntimeGameReadResult.NoDeathsRecorded(observation, metadata, state.BlackMythWukongSave.LocalPath)
                    : RuntimeGameReadResult.Synced(observation, metadata, state.BlackMythWukongSave.LocalPath);
            }
            void Deliver(RuntimeGameReadResult result) => session.CompleteRead(session.BeginRead(state), state, result,
                desktop.ApplyRuntimeReaderResult, publication =>
                {
                    text.PublishRuntimeObservation(state, publication);
                    overlay.PublishAccepted(state, publication);
                });

            Deliver(Read(100, 1, 10));
            Assert.True(await written.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            string? metadata = desktop.BlackMythWukongSaveMetadataText;
            HostedOverlayEnvelope confirmed = Snapshot(overlay);
            RuntimeGameReadResult lower = Read(lowerValue, 2, 20);
            Deliver(lower);
            Assert.Equal("100", desktop.TotalDeathsText);
            Assert.Equal("Confirming lower death count", desktop.RuntimeReaderStatusText);
            Assert.Equal(metadata, desktop.BlackMythWukongSaveMetadataText);
            Assert.Same(confirmed, Snapshot(overlay));
            Assert.Equal("Total Deaths: 100", await File.ReadAllTextAsync(path));
            Deliver(RuntimeGameReadResult.Cached(lower));
            Assert.Equal("100", desktop.TotalDeathsText);
            Assert.Equal("Confirming lower death count", desktop.RuntimeReaderStatusText);
            Assert.Equal(metadata, desktop.BlackMythWukongSaveMetadataText);
            Assert.Same(confirmed, Snapshot(overlay));
            Assert.Equal(1, Volatile.Read(ref writes));

            written = NewCompletion();
            Deliver(Read(lowerValue, 3, 30));
            Assert.True(await written.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(lowerValue == 0 ? DesktopTrackerViewModel.NoDeathsRecordedMessage : "90", desktop.TotalDeathsText);
            Assert.Equal("Synced", desktop.RuntimeReaderStatusText);
            Assert.NotEqual(metadata, desktop.BlackMythWukongSaveMetadataText);
            Assert.Equal(lowerValue.ToString(System.Globalization.CultureInfo.InvariantCulture), Snapshot(overlay).Death!.Value);
            Assert.NotSame(confirmed, Snapshot(overlay));
            Assert.Equal($"Total Deaths: {lowerValue}", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CommittedSelectionsBetweenPollsInvalidateOldGeneration() => await RunOnDispatcherAsync(async () =>
    {
        PersistentTrackerState state = new(PersistentTrackerState.CurrentSchemaVersion, GameId.Bloodborne, OverlayConfiguration.Default);
        var session = new RuntimePublicationSession();
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(state),
            new DesktopStateChangePublisher(Dispatcher.CurrentDispatcher, session, new NullPublisher()));
        var desktop = new DesktopTrackerViewModel(coordinator);
        await desktop.InitializeAsync();
        var oldRead = session.BeginRead(state);
        _ = await coordinator.SubmitAsync(new SelectGameCommand(GameId.DemonsSouls));
        TrackerCommandExecutionResult back = await coordinator.SubmitAsync(new SelectGameCommand(GameId.Bloodborne));
        desktop.ApplyImportedCommittedState(back.CommittedState!);
        bool published = false;
        session.CompleteRead(oldRead, desktop.CurrentState!, RuntimeGameReadResult.Synced(new RuntimeGameObservation(
            state.SelectedGameId, 100, DateTimeOffset.UtcNow, state.SelectedGameId.Value)),
            _ => published = true, _ => published = true);
        Assert.False(published);
    });

    [Theory]
    [InlineData("ds1")]
    [InlineData("ds2")]
    [InlineData("ds3")]
    [InlineData("sekiro")]
    [InlineData("bloodborne")]
    [InlineData("elden_ring")]
    [InlineData("black_myth_wukong")]
    [InlineData("lies_of_p")]
    public async Task AllConsumersKeepAcceptedBaseAcrossPendingConfigurationAndAcceptance(string game) => await RunOnDispatcherAsync(async () =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"souls-consumers-{Guid.NewGuid():N}.txt");
        try
        {
            PersistentTrackerState state = RuntimePublicationSessionTests.Selected(GameId.Parse(game));
            await using var text = new TextExportStatePublisher();
            await using var overlay = new HostedDesktopPublisher(null, state);
            var session = new RuntimePublicationSession();
            await using var coordinator = new SerializedTrackerCoordinator(new Repository(state),
                new DesktopStateChangePublisher(Dispatcher.CurrentDispatcher, session,
                    new CompositeTrackerStateChangePublisher(overlay, text)));
            var desktop = new DesktopTrackerViewModel(coordinator);
            await desktop.InitializeAsync();
            TaskCompletionSource<bool> written = NewCompletion();
            text.WriteCompleted += (_, success) => written.TrySetResult(success);
            state = await coordinator.SetTextExportConfigurationAsync(new TextExportConfiguration(path, true));
            desktop.ApplyImportedCommittedState(state);
            Assert.True(await written.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            int second = 0;
            RuntimeGameReadResult Read(long value) => RuntimeGameReadResult.Synced(new RuntimeGameObservation(
                state.SelectedGameId, value, DateTimeOffset.UnixEpoch.AddSeconds(++second), EffectiveDeathTotalResult.SourceIdentityFor(state)));
            void Deliver(RuntimeGameReadResult result) => session.CompleteRead(session.BeginRead(state), state, result,
                desktop.ApplyRuntimeReaderResult, publication =>
                {
                    text.PublishRuntimeObservation(state, publication);
                    overlay.PublishAccepted(state, publication);
                });
            written = NewCompletion();
            Deliver(Read(100));
            Assert.True(await written.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            HostedOverlayEnvelope original = Snapshot(overlay);
            RuntimeGameReadResult lower = Read(90);
            Deliver(lower);
            Deliver(RuntimeGameReadResult.Cached(lower));
            Assert.Equal("100", desktop.TotalDeathsText);
            Assert.Equal(game is "elden_ring" or "black_myth_wukong" or "lies_of_p"
                ? "Confirming lower death count" : "Synced", desktop.RuntimeReaderStatusText);
            Assert.Same(original, Snapshot(overlay));
            Assert.Equal("Total Deaths: 100", await File.ReadAllTextAsync(path));

            var completingDuringCommit = session.BeginRead(state);
            PersistentTrackerState unapplied = state;
            written = NewCompletion();
            long adjustment = 0;
            if (state.SelectedGameId == GameId.EldenRing)
            {
                TrackerCommandExecutionResult result = await coordinator.SubmitAsync(new AdjustEldenRingMissedDeathsCommand(true));
                state = result.CommittedState!;
                adjustment = 1;
            }
            else
            {
                // An unrelated configuration commit must not restart confirmation.
                state = await coordinator.SetTextExportConfigurationAsync(state.TextExports);
            }
            if (state.SelectedGameId == GameId.EldenRing)
                session.CompleteRead(completingDuringCommit, unapplied, Read(120),
                    _ => Assert.Fail("Completion raced the committed desktop state"),
                    _ => Assert.Fail("Completion rolled back committed output state"));
            desktop.ApplyImportedCommittedState(state);
            Assert.True(await written.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal((100 + adjustment).ToString(System.Globalization.CultureInfo.InvariantCulture), desktop.TotalDeathsText);
            Assert.Equal((100 + adjustment).ToString(System.Globalization.CultureInfo.InvariantCulture), Snapshot(overlay).Death!.Value);
            Assert.Equal($"Total Deaths: {100 + adjustment}", await File.ReadAllTextAsync(path));
            written = NewCompletion();
            Deliver(Read(90));
            Assert.True(await written.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal((90 + adjustment).ToString(System.Globalization.CultureInfo.InvariantCulture), desktop.TotalDeathsText);
            Assert.Equal((90 + adjustment).ToString(System.Globalization.CultureInfo.InvariantCulture), Snapshot(overlay).Death!.Value);
            Assert.Equal($"Total Deaths: {90 + adjustment}", await File.ReadAllTextAsync(path));
            if (state.SelectedGameId == GameId.EldenRing || state.SelectedGameId == GameId.BlackMythWukong || state.SelectedGameId == GameId.LiesOfP)
            {
                Deliver(Read(80));
                var delayed = session.BeginRead(state);
                RuntimeGameReadResult oldSource = Read(80);
                written = NewCompletion();
                state = state.SelectedGameId == GameId.EldenRing
                    ? await coordinator.SetEldenRingSaveConfigurationAsync(new EldenRingSaveConfiguration(state.EldenRingSave.LocalPath, 2))
                    : state.SelectedGameId == GameId.BlackMythWukong
                        ? await coordinator.SetBlackMythWukongSaveConfigurationAsync(new BlackMythWukongSaveConfiguration("C:/saves/new/ArchiveSaveFile.1.sav"))
                        : await coordinator.SetLiesOfPSaveConfigurationAsync(new LiesOfPSaveConfiguration("C:/saves/test/SaveData-1_Character_2.sav"));
                desktop.ApplyImportedCommittedState(state);
                Assert.True(await written.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.False(desktop.IsTotalDeathsValueNumeric);
                Assert.NotEqual("Confirming lower death count", desktop.RuntimeReaderStatusText);
                HostedOverlayEnvelope unavailable = Snapshot(overlay);
                Assert.Equal("unavailable", unavailable.Death!.Availability);
                if (state.SelectedGameId != GameId.EldenRing) Assert.Equal(string.Empty, await File.ReadAllTextAsync(path));
                session.CompleteRead(delayed, state, oldSource, _ => Assert.Fail("Old source resurrected in Desktop"),
                    _ => Assert.Fail("Old source resurrected in outputs"));
                Assert.Same(unavailable, Snapshot(overlay));
                written = NewCompletion();
                Deliver(Read(5));
                Assert.True(await written.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal("5", desktop.TotalDeathsText);
                Assert.Equal("5", Snapshot(overlay).Death!.Value);
                Assert.Equal("Total Deaths: 5", await File.ReadAllTextAsync(path));
            }
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public async Task OrdinalSourceChangeClearsPendingStatusImmediately()
    {
        PersistentTrackerState state = RuntimePublicationSessionTests.Selected(GameId.BlackMythWukong);
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(state), new NullPublisher());
        var desktop = new DesktopTrackerViewModel(coordinator);
        await desktop.InitializeAsync();
        var session = new RuntimePublicationSession();

        for (int i = 0; i < 2; i++)
        {
            RuntimeGameReadResult result = RuntimeGameReadResult.Synced(new RuntimeGameObservation(state.SelectedGameId,
                i == 0 ? 100 : 90, DateTimeOffset.UnixEpoch.AddSeconds(i), EffectiveDeathTotalResult.SourceIdentityFor(state)));
            session.CompleteRead(session.BeginRead(state), state, result, desktop.ApplyRuntimeReaderResult, _ => { });
        }
        Assert.Equal("Confirming lower death count", desktop.RuntimeReaderStatusText);
        desktop.ApplyImportedCommittedState(RuntimePublicationSessionTests.Selected(GameId.BlackMythWukong, "TEST"));
        Assert.NotEqual("Confirming lower death count", desktop.RuntimeReaderStatusText);
        Assert.False(desktop.IsTotalDeathsValueNumeric);
    }

    [Fact]
    public async Task NotificationSilentCommitDoesNotFreezeRuntimePublication() => await RunOnDispatcherAsync(async () =>
    {
        PersistentTrackerState state = RuntimePublicationSessionTests.Selected(GameId.EldenRing);
        var session = new RuntimePublicationSession();
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(state),
            new DesktopStateChangePublisher(Dispatcher.CurrentDispatcher, session, new NullPublisher()));
        var desktop = new DesktopTrackerViewModel(coordinator);
        await desktop.InitializeAsync();
        session.SelectState(state);
        _ = await coordinator.SetGlobalHotkeysAsync(state.GlobalHotkeys);
        TrackerCommandExecutionResult unchanged = await coordinator.SubmitAsync(new SelectGameCommand(GameId.EldenRing));
        desktop.ApplyImportedCommittedState(unchanged.CommittedState!);
        var read = session.BeginRead(session.CurrentState!);
        bool published = false;
        session.CompleteRead(read, desktop.CurrentState!, RuntimeGameReadResult.Synced(new RuntimeGameObservation(
            GameId.EldenRing, 100, DateTimeOffset.UtcNow, EffectiveDeathTotalResult.SourceIdentityFor(state))),
            desktop.ApplyRuntimeReaderResult, _ => published = true);
        Assert.True(published);
        Assert.Equal("100", desktop.TotalDeathsText);
    });

    private static async Task RunOnDispatcherAsync(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    // Inspect the actual hosted publisher's latest envelope without network I/O.
    private static HostedOverlayEnvelope Snapshot(HostedDesktopPublisher publisher) =>
        (HostedOverlayEnvelope)typeof(HostedDesktopPublisher).GetField("latest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(publisher)!;
    private static TaskCompletionSource<bool> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Repository(PersistentTrackerState state) : ITrackerStateRepository
    {
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(state));
        public Task SaveAsync(PersistentTrackerState value, CancellationToken cancellationToken = default) { state = value; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
