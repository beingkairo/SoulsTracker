using System.IO;

using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using SoulsTracker.Overlay;

namespace SoulsTracker.Desktop.Tests;

public sealed class RuntimeSourcePublicationTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "")]
    [InlineData(true, "unknown")]
    public async Task UnprovenSourceCannotPassSharedPublicationBoundary(bool lies, string? identity)
    {
        PersistentTrackerState state = State(lies, "old");
        RuntimeGameReadResult read = RuntimeGameReadResult.Synced(new RuntimeGameObservation(
            state.SelectedGameId, 42, DateTimeOffset.UtcNow, identity));

        RuntimeGameReadResult? publication = Normalize(state, read);

        Assert.Null(publication);
        await AssertConsumersAsync(state, publication, null);
        await AssertConsumersAsync(state, read, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedCompletionFromPreviousSaveIsRejectedAfterSelectionChanges(bool lies)
    {
        PersistentTrackerState oldState = State(lies, "old");
        PersistentTrackerState newState = State(lies, "new");
        RuntimeGameReadResult delayed = RuntimeGameReadResult.Synced(new RuntimeGameObservation(
            oldState.SelectedGameId, 91, DateTimeOffset.UtcNow,
            EffectiveDeathTotalResult.SourceIdentityFor(oldState)));

        RuntimeGameReadResult? publication = Normalize(newState, delayed);

        Assert.Null(publication);
        await AssertConsumersAsync(newState, publication, null);
    }

    [Fact]
    public async Task StaleCompletionCannotOverwriteNewerCurrentSourceResult()
    {
        PersistentTrackerState oldState = State(false, "old");
        PersistentTrackerState currentState = State(false, "new");
        RuntimeGameReadResult current = RuntimeGameReadResult.Synced(new RuntimeGameObservation(
            currentState.SelectedGameId, 12, DateTimeOffset.UtcNow,
            EffectiveDeathTotalResult.SourceIdentityFor(currentState)));
        RuntimeGameReadResult delayedOld = RuntimeGameReadResult.Synced(new RuntimeGameObservation(
            oldState.SelectedGameId, 91, DateTimeOffset.UtcNow,
            EffectiveDeathTotalResult.SourceIdentityFor(oldState)));

        RuntimeGameReadResult? published = Normalize(currentState, current);
        RuntimeGameReadResult? stale = Normalize(currentState, delayedOld);

        Assert.NotNull(published);
        Assert.Null(stale);
        await AssertConsumersAsync(currentState, published, 12);
    }

    private static RuntimeGameReadResult? Normalize(PersistentTrackerState state, RuntimeGameReadResult? read) =>
        App.NormalizeRuntimePublication(state, read);

    private static PersistentTrackerState State(bool lies, string profile, int character = 1, int member = 1, string? export = null) =>
        new(PersistentTrackerState.CurrentSchemaVersion, lies ? GameId.LiesOfP : GameId.BlackMythWukong,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne), OverlayConfiguration.Default,
            textExports: new TextExportConfiguration(export, export is not null),
            blackMythWukongSave: new BlackMythWukongSaveConfiguration($"C:/saves/{profile}/ArchiveSaveFile.1.sav"),
            liesOfPSave: new LiesOfPSaveConfiguration($"C:/saves/{profile}/SaveData-{character}_Character_{member}.sav"));

    private static async Task AssertConsumersAsync(PersistentTrackerState state, RuntimeGameReadResult? publication, long? expected)
    {
        await using var coordinator = new SerializedTrackerCoordinator(new MemoryRepository(state), new NullPublisher());
        var tracker = new DesktopTrackerViewModel(coordinator);
        await tracker.InitializeAsync();
        tracker.ApplyRuntimeReaderResult(publication);
        Assert.Equal(expected.HasValue, tracker.IsTotalDeathsValueNumeric);
        if (expected.HasValue) Assert.Equal(expected.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), tracker.TotalDeathsText);
        Assert.Equal(expected, OverlaySnapshotFactory.Create(state, publication?.Observation, 1).TotalDeaths.Value);

        string export = Path.Combine(Path.GetTempPath(), $"souls-source-{Guid.NewGuid():N}.txt");
        try
        {
            PersistentTrackerState exportState = new(state.SchemaVersion, state.SelectedGameId,
                state.ManualBloodborneDeathCounter, state.OverlayConfiguration,
                textExports: new TextExportConfiguration(export, true),
                blackMythWukongSave: state.BlackMythWukongSave, liesOfPSave: state.LiesOfPSave);
            await File.WriteAllTextAsync(export, "Total Deaths: 42");
            await using (var publisher = new TextExportStatePublisher())
            {
                publisher.PublishRuntimeObservation(exportState, publication);
            }
            Assert.Equal(expected.HasValue ? $"Total Deaths: {expected.Value}" : string.Empty, await File.ReadAllTextAsync(export));
        }
        finally { File.Delete(export); }
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryRepository(PersistentTrackerState state) : ITrackerStateRepository
    {
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(state));
        public Task SaveAsync(PersistentTrackerState value, CancellationToken cancellationToken = default)
        {
            state = value;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
