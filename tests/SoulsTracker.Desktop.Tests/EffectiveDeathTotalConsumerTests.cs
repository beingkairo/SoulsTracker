using System.IO;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Overlay;

namespace SoulsTracker.Desktop.Tests;

public sealed class EffectiveDeathTotalConsumerTests
{
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
            await coordinator.SubmitAsync(new IncrementManualBloodborneDeathsCommand());
            await coordinator.SubmitAsync(new IncrementManualBloodborneDeathsCommand());
            await coordinator.SubmitAsync(new DecrementManualBloodborneDeathsCommand());
        }

        Assert.Equal(1, repository.State.ManualDemonsSoulsDeathCounter.Value);
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryRepository : ITrackerStateRepository
    {
        public PersistentTrackerState State { get; private set; } = PersistentTrackerState.Default;
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(State));
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
        {
            State = state;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}