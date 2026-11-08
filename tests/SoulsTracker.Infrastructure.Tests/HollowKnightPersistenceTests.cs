using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Infrastructure.Tests;

public sealed class HollowKnightPersistenceTests
{
    [Fact]
    public async Task EventsCommitBeforeDeliveryAndSurviveSettingsSelectionAndApplicationRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SoulsTracker-tests", Guid.NewGuid().ToString("N"));
        Guid id = Guid.NewGuid();
        var publisher = new Publisher();
        try
        {
            await using (var repository = new SqliteTrackerStateRepository(directory, "tracker.db"))
            await using (var coordinator = new SerializedTrackerCoordinator(repository, publisher))
            {
                await coordinator.InitializeAsync();
                await coordinator.SubmitAsync(new SelectGameCommand(GameId.HollowKnight));
                TrackerCommandExecutionResult death = await coordinator.SubmitAsync(new RecordHollowKnightDeathCommand(id));
                Assert.Equal(TrackerCommandExecutionStatus.Applied, death.Status);
                Assert.Equal(1, death.CommittedState!.HollowKnightDeaths.Count);
                Assert.Equal(1, publisher.PublishedDeaths);
                Assert.Equal(TrackerCommandExecutionStatus.NoChange, (await coordinator.SubmitAsync(new RecordHollowKnightDeathCommand(id))).Status);
                await coordinator.SetGlobalHotkeysAsync(GlobalHotkeyConfiguration.Default);
                await coordinator.SetTextExportConfigurationAsync(TextExportConfiguration.Default);
                await coordinator.SetEldenRingSaveConfigurationAsync(EldenRingSaveConfiguration.Default);
                await coordinator.SetBlackMythWukongSaveConfigurationAsync(BlackMythWukongSaveConfiguration.Default);
                await coordinator.SetLiesOfPSaveConfigurationAsync(LiesOfPSaveConfiguration.Default);
                await coordinator.SubmitAsync(new SetCheckForUpdatesOnStartupCommand(true));
                await coordinator.SubmitAsync(new UpdateOverlayPresentationCommand(true, true));
                await coordinator.SubmitAsync(new SelectGameCommand(GameId.Bloodborne));
            }
            await using var restartedRepository = new SqliteTrackerStateRepository(directory, "tracker.db");
            await using var restarted = new SerializedTrackerCoordinator(restartedRepository, publisher);
            PersistentTrackerState loaded = (await restarted.InitializeAsync()).State!;
            Assert.Equal(GameId.Bloodborne, loaded.SelectedGameId);
            Assert.Equal(id, loaded.HollowKnightDeaths.LastEventId);
            Assert.Equal(1, loaded.HollowKnightDeaths.Count);
            await restarted.SubmitAsync(new SelectGameCommand(GameId.HollowKnight));
            Assert.Equal(TrackerCommandExecutionStatus.NoChange, (await restarted.SubmitAsync(new RecordHollowKnightDeathCommand(id))).Status);
            TrackerCommandExecutionResult next = await restarted.SubmitAsync(new RecordHollowKnightDeathCommand(Guid.NewGuid()));
            Assert.Equal(2, next.CommittedState!.HollowKnightDeaths.Count);
            Assert.Equal(0, next.CommittedState.ManualDemonsSoulsDeathCounter.Value);
            Assert.Equal(2, publisher.PublishedDeaths);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SaveFailureCannotPublishOrLosePendingRetryAndDeliveryFailureCannotRecount()
    {
        var repository = new Repository();
        var publisher = new Publisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        await coordinator.InitializeAsync();
        Guid id = Guid.NewGuid();
        repository.Fail = true;
        Assert.Equal(TrackerCommandExecutionStatus.SaveFailed, (await coordinator.SubmitAsync(new RecordHollowKnightDeathCommand(id))).Status);
        Assert.Equal(0, publisher.PublishedDeaths);
        Assert.Equal(0, repository.State.HollowKnightDeaths.Count);
        repository.Fail = false;
        publisher.Fail = true;
        Assert.Equal(TrackerCommandExecutionStatus.DeliveryFailed, (await coordinator.SubmitAsync(new RecordHollowKnightDeathCommand(id))).Status);
        Assert.Equal(1, repository.State.HollowKnightDeaths.Count);
        publisher.Fail = false;
        Assert.Equal(TrackerCommandExecutionStatus.NoChange, (await coordinator.SubmitAsync(new RecordHollowKnightDeathCommand(id))).Status);
        Assert.Equal(1, repository.State.HollowKnightDeaths.Count);
    }

    [Fact]
    public void ObservedCountsCannotBeMistakenForLifetimeMemoryObservations()
    {
        Assert.Equal(GameTrackingMode.ObservedDeaths, GameCatalog.GetRequired(GameId.HollowKnight).TrackingMode);
        Assert.Throws<ArgumentException>(() => new RuntimeGameObservation(GameId.HollowKnight, 1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HollowKnightDeaths(-1, null));
        Assert.Throws<ArgumentException>(() => HollowKnightDeaths.Empty.Record(Guid.Empty));
    }

    private sealed class Repository : ITrackerStateRepository
    {
        internal PersistentTrackerState State { get; private set; } = PersistentTrackerState.Default;
        internal bool Fail { get; set; }
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(State));
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Synthetic save failure.");
            State = state;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Publisher : ITrackerStateChangePublisher
    {
        internal int PublishedDeaths { get; private set; }
        internal bool Fail { get; set; }
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Synthetic delivery failure.");
            if (notification.CommandType == TrackerCommandType.RecordHollowKnightDeath)
            {
                Assert.True(notification.State.HollowKnightDeaths.Count > 0);
                PublishedDeaths++;
            }
            return Task.CompletedTask;
        }
    }
}
