using System.IO;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using SoulsTracker.Overlay;

namespace SoulsTracker.Desktop.Tests;

public sealed class EffectiveDeathTotalConsumerTests
{
    [Theory]
    [InlineData(12, EffectiveDeathTotalStatus.Synced, 12)]
    [InlineData(0, EffectiveDeathTotalStatus.Zero, 0)]
    public void OverlayAndEffectiveResultAgreeForNamedSyncedMatrixScenarios(int observed, EffectiveDeathTotalStatus status, int total)
    {
        PersistentTrackerState state = new(PersistentTrackerState.CurrentSchemaVersion, GameId.BlackMythWukong,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), OverlayConfiguration.Default,
            blackMythWukongSave: new BlackMythWukongSaveConfiguration("/saves/ArchiveSaveFile.1.sav"));
        RuntimeGameObservation observation = new(GameId.BlackMythWukong, new GameLifetimeDeathTotal(observed), DateTimeOffset.UtcNow, EffectiveDeathTotalResult.SourceIdentityFor(state));
        EffectiveDeathTotalResult result = EffectiveDeathTotalResult.Resolve(state, observation);
        OverlaySnapshot snapshot = OverlaySnapshotFactory.Create(state, observation, 1);

        Assert.Equal(status, result.Status);
        Assert.Equal(total, result.EffectiveDisplayedTotal);
        Assert.Equal(total, snapshot.TotalDeaths.Value);
    }

    [Fact]
    public void MatrixDocumentsUnavailableUnreadableAndWaitingAsUnavailable()
    {
        PersistentTrackerState state = PersistentTrackerState.Default;
        foreach (string scenario in new[] { "unavailable", "unreadable", "waiting" })
        {
            EffectiveDeathTotalResult result = EffectiveDeathTotalResult.Resolve(state, null);
            Assert.Equal(EffectiveDeathTotalStatus.Unavailable, result.Status); // scenario is intentionally represented by the null-observation seam
            Assert.Equal(0, OverlaySnapshotFactory.Create(state, null, 1).TotalDeaths.Value);
        }
    }
    [Fact]
    public void OverlaySnapshotUsesManualEffectiveTotalWhenObservationIsUnavailable()
    {
        PersistentTrackerState state = PersistentTrackerState.Default;

        OverlaySnapshot snapshot = OverlaySnapshotFactory.Create(state, observation: null, sequenceNumber: 1);

        Assert.Equal(0, snapshot.TotalDeaths.Value);
        Assert.Equal(GameId.DemonsSouls, snapshot.TotalDeaths.GameId);
    }

    [Fact]
    public async Task TextExportUsesEffectiveTotalAndClearsSaveBackedUnavailableValue()
    {
        string path = Path.Combine(Path.GetTempPath(), $"souls-tracker-{Guid.NewGuid():N}.txt");
        try
        {
            PersistentTrackerState state = new(
                PersistentTrackerState.CurrentSchemaVersion,
                GameId.DemonsSouls,
                ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 4),
                OverlayConfiguration.Default,
                textExports: new TextExportConfiguration(path, true));

            Assert.True(await TextExportStatePublisher.WriteAsync(state));
            Assert.Equal("Total Deaths: 0", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ManualIncrementAndDecrementArePersistedThroughCoordinatorReload()
    {
        var repository = new MemoryRepository();
        await using (var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher()))
        {
            await coordinator.InitializeAsync();
            await coordinator.SubmitAsync(new SelectGameCommand(GameId.DemonsSouls));
            await coordinator.SubmitAsync(new IncrementManualBloodborneDeathsCommand());
            await coordinator.SubmitAsync(new IncrementManualBloodborneDeathsCommand());
            await coordinator.SubmitAsync(new DecrementManualBloodborneDeathsCommand());
        }

        Assert.Equal(GameId.DemonsSouls, repository.State.SelectedGameId);
        Assert.Equal(1, repository.State.ManualDemonsSoulsDeathCounter.Value);
        await using var reloaded = new SerializedTrackerCoordinator(repository, new NullPublisher());
        TrackerStateLoadResult load = await reloaded.InitializeAsync();
        Assert.True(load.IsSuccess);
        Assert.Equal(1, load.State!.GetManualDeathCounter(GameId.DemonsSouls).Value);
    }

    [Fact]
    public async Task DesktopViewModelDisplaysTheSelectedManualEffectiveTotal()
    {
        var repository = new MemoryRepository();
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var tracker = new DesktopTrackerViewModel(coordinator);

        await tracker.InitializeAsync();
        await tracker.SelectGameAsync(tracker.GameChoices.Single(choice => choice.GameId == GameId.DemonsSouls));
        await tracker.IncrementManualDeathsAsync();

        Assert.Equal(GameId.DemonsSouls, tracker.SelectedGame!.GameId);
        Assert.Equal(1, tracker.ManualDeaths);
        Assert.Equal("1", tracker.TotalDeathsText);
        Assert.True(tracker.IsTotalDeathsValueNumeric);
    }

    [Theory]
    [InlineData("synced", 12L)]
    [InlineData("zero", 0L)]
    [InlineData("unavailable", null)]
    [InlineData("unreadable", null)]
    [InlineData("waiting", null)]
    [InlineData("source-mismatch", null)]
    public async Task WukongPublicationMatrixExercisesAllConsumers(string scenario, long? expectedTotal)
    {
        string path = Path.Combine(Path.GetTempPath(), $"souls-tracker-{Guid.NewGuid():N}.txt");
        try
        {
            PersistentTrackerState state = new(PersistentTrackerState.CurrentSchemaVersion, GameId.BlackMythWukong,
                ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), OverlayConfiguration.Default,
                textExports: new TextExportConfiguration(path, true),
                blackMythWukongSave: new BlackMythWukongSaveConfiguration("C:/saves/ArchiveSaveFile.1.sav"));
            RuntimeGameObservation observation = new(GameId.BlackMythWukong, new GameLifetimeDeathTotal(scenario == "zero" ? 0 : 12),
                DateTimeOffset.UtcNow, scenario == "source-mismatch" ? "other-save" : EffectiveDeathTotalResult.SourceIdentityFor(state));
            RuntimeGameReadResult read = scenario switch
            {
                "unavailable" => RuntimeGameReadResult.Unavailable(state.SelectedGameId),
                "unreadable" => RuntimeGameReadResult.SelectedSaveUnreadable(state.SelectedGameId),
                "waiting" => RuntimeGameReadResult.WaitingForSaveFile(state.SelectedGameId),
                _ => RuntimeGameReadResult.Synced(observation),
            };
            // Unreadable/waiting retain reader-specific Desktop text; both have no observation at the shared boundary.
            EffectiveDeathTotalResult result = EffectiveDeathTotalResult.Resolve(state, read.Observation);
            Assert.Equal(expectedTotal, result.EffectiveDisplayedTotal);
            Assert.Equal(scenario == "source-mismatch" ? EffectiveDeathTotalStatus.SourceMismatch
                : expectedTotal == 0 ? EffectiveDeathTotalStatus.Zero
                : expectedTotal.HasValue ? EffectiveDeathTotalStatus.Synced : EffectiveDeathTotalStatus.Unavailable, result.Status);

            await using var coordinator = new SerializedTrackerCoordinator(new MemoryRepository(state), new NullPublisher());
            var tracker = new DesktopTrackerViewModel(coordinator);
            await tracker.InitializeAsync();
            tracker.ApplyRuntimeReaderResult(read);
            Assert.Equal(expectedTotal.HasValue, tracker.IsTotalDeathsValueNumeric);
            if (expectedTotal.HasValue) Assert.Equal(expectedTotal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), tracker.TotalDeathsText);

            await File.WriteAllTextAsync(path, "previous value");
            await using (var publisher = new TextExportStatePublisher())
            {
                publisher.PublishRuntimeObservation(state, read);
            }
            Assert.Equal(expectedTotal.HasValue ? $"Total Deaths: {expectedTotal.Value}" : string.Empty, await File.ReadAllTextAsync(path));

            OverlaySnapshot snapshot = OverlaySnapshotFactory.Create(state, read.Observation, 1);
            Assert.Equal(expectedTotal, snapshot.TotalDeaths.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EldenRingAdjustmentRequiresSavedBaseAcrossConsumers(bool hasSavedBase)
    {
        string path = Path.Combine(Path.GetTempPath(), $"souls-tracker-{Guid.NewGuid():N}.txt");
        try
        {
            PersistentTrackerState state = new(PersistentTrackerState.CurrentSchemaVersion, GameId.EldenRing,
                ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), OverlayConfiguration.Default,
                textExports: new TextExportConfiguration(path, true), eldenRingNoticeAcknowledged: true,
                eldenRingSave: new EldenRingSaveConfiguration("C:/saves/ER0000.sl2", 1),
                eldenRingMissedDeathAdjustments: new EldenRingMissedDeathAdjustments([new EldenRingMissedDeathAdjustment("C:/saves/ER0000.sl2", 1, 3)]));
            RuntimeGameReadResult? read = hasSavedBase ? RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.EldenRing,
                new GameLifetimeDeathTotal(12), DateTimeOffset.UtcNow, EffectiveDeathTotalResult.SourceIdentityFor(state))) : null;
            EffectiveDeathTotalResult result = EffectiveDeathTotalResult.Resolve(state, read?.Observation);
            Assert.Equal(hasSavedBase ? 15L : (long?)null, result.EffectiveDisplayedTotal);
            Assert.Equal(hasSavedBase ? EffectiveDeathTotalStatus.Synced : EffectiveDeathTotalStatus.Unavailable, result.Status);
            Assert.Equal(3, state.EldenRingMissedDeathAdjustments.Get(state.EldenRingSave));

            await using var coordinator = new SerializedTrackerCoordinator(new MemoryRepository(state), new NullPublisher());
            var tracker = new DesktopTrackerViewModel(coordinator);
            await tracker.InitializeAsync();
            tracker.ApplyRuntimeReaderResult(read);
            Assert.Equal(hasSavedBase, tracker.IsTotalDeathsValueNumeric);
            if (hasSavedBase) Assert.Equal("15", tracker.TotalDeathsText);

            OverlaySnapshot snapshot = OverlaySnapshotFactory.Create(state, read?.Observation, 1);
            // Existing Elden Ring unavailable transport uses a zero placeholder, never the adjustment alone.
            Assert.Equal(hasSavedBase ? 15 : 0, snapshot.TotalDeaths.Value);
            Assert.Equal(hasSavedBase ? TotalDeathsDisplaySource.GameLifetimeReader : TotalDeathsDisplaySource.Unavailable, snapshot.TotalDeaths.Source);
            await using (var publisher = new TextExportStatePublisher())
            {
                publisher.PublishRuntimeObservation(state, read);
            }
            if (hasSavedBase) Assert.Equal("Total Deaths: 15", await File.ReadAllTextAsync(path));
            else Assert.False(File.Exists(path)); // Existing unavailable Elden Ring export does not write a partial value.
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryRepository(PersistentTrackerState? initialState = null) : ITrackerStateRepository
    {
        public PersistentTrackerState State { get; private set; } = initialState ?? PersistentTrackerState.Default;
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(State));
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
        {
            State = state;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}