using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class ReaderStatusPresentationTests
{
    [Fact]
    public void WaitingExplanationUsesOrdinarySentencePunctuation() =>
        Assert.Equal("Unavailable, waiting for active character.", DesktopTrackerViewModel.GameTotalDeathsWaitingForActiveCharacterMessage);

    [Theory]
    [InlineData("er")]
    [InlineData("wk")]
    [InlineData("lp")]
    public async Task ReadableSaveZeroSeparatesExplanationFromReaderStatus(string game)
    {
        using var fixture = new SaveDirectoryWorkflowTests();
        string path = SaveDirectoryWorkflowTests.CreateSave(game, fixture.Root);
        var repository = new SaveDirectoryWorkflowTests.MemoryRepository(SaveDirectoryWorkflowTests.Game(game));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var desktop = SaveDirectoryWorkflowTests.CreateViewModel(coordinator);
        await desktop.InitializeAsync();
        await SaveDirectoryWorkflowTests.Choose(desktop, game, fixture.Root);
        var zero = new RuntimeGameObservation(repository.State.SelectedGameId, 0, DateTimeOffset.UtcNow,
            EffectiveDeathTotalResult.SourceIdentityFor(repository.State));
        desktop.ApplyRuntimeReaderResult(RuntimeGameReadResult.Synced(zero));
        Assert.Equal("Synced", desktop.RuntimeReaderStatusText);
        Assert.Equal("0", desktop.TotalDeathsText);
        desktop.ApplyRuntimeReaderResult(RuntimeGameReadResult.NoDeathsRecorded(zero));
        Assert.Equal("Synced", desktop.RuntimeReaderStatusText);
        Assert.Equal("No deaths recorded yet, the tracker will update after your first death", desktop.TotalDeathsText);
        desktop.ApplyRuntimeReaderResult(RuntimeGameReadResult.Cached(RuntimeGameReadResult.NoDeathsRecorded(zero)));
        Assert.Equal("Using last confirmed save data", desktop.RuntimeReaderStatusText);
        desktop.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(repository.State.SelectedGameId));
        Assert.NotEqual("Synced", desktop.RuntimeReaderStatusText);
        Assert.NotEqual(DesktopTrackerViewModel.NoDeathsRecordedMessage, desktop.TotalDeathsText);
    }

    [Theory]
    [InlineData("ds1")]
    [InlineData("ds2")]
    [InlineData("ds3")]
    [InlineData("bloodborne")]
    [InlineData("sekiro")]
    public async Task InternalCharacterStateDoesNotSurfaceWaitingAndReadableZeroRemainsNumeric(string game)
    {
        GameId id = GameId.Parse(game);
        var state = new PersistentTrackerState(PersistentTrackerState.CurrentSchemaVersion, id, OverlayConfiguration.Default);
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(state), new NullPublisher());
        var desktop = new DesktopTrackerViewModel(coordinator);
        await desktop.InitializeAsync();
        desktop.ApplyRuntimeReaderResult(RuntimeGameReadResult.WaitingForActiveCharacter(id));
        // Bloodborne's internal waiting result means its total was unreadable/invalid.
        Assert.Equal(id == GameId.Bloodborne ? "Game unavailable" : "Synced", desktop.RuntimeReaderStatusText);
        Assert.DoesNotContain("waiting", desktop.TotalDeathsText, StringComparison.OrdinalIgnoreCase);
        var zero = new RuntimeGameObservation(id, 0, DateTimeOffset.UtcNow, id.Value);
        desktop.ApplyRuntimeReaderResult(id == GameId.Bloodborne
            ? RuntimeGameReadResult.NoDeathsRecorded(zero) : RuntimeGameReadResult.Synced(zero));
        Assert.Equal("Synced", desktop.RuntimeReaderStatusText);
        Assert.Equal("0", desktop.TotalDeathsText);
        desktop.ApplyRuntimeReaderResult(null);
        Assert.Equal("Game unavailable", desktop.RuntimeReaderStatusText);
    }

    [Fact]
    public async Task ManualSelectionAlwaysShowsManualDespiteAutomaticResidue()
    {
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(PersistentTrackerState.Default), new NullPublisher());
        var desktop = new DesktopTrackerViewModel(coordinator);
        await desktop.InitializeAsync();
        Assert.Equal("Manual", desktop.RuntimeReaderStatusText);
        foreach (RuntimeGameReadResult? result in new RuntimeGameReadResult?[] { null,
            RuntimeGameReadResult.WaitingForActiveCharacter(GameId.Ds1),
            RuntimeGameReadResult.PendingLowerValue(RuntimeGameReadResult.Synced(new RuntimeGameObservation(GameId.Ds1, 10, DateTimeOffset.UtcNow, GameId.Ds1.Value))) })
        {
            desktop.ApplyRuntimeReaderResult(result);
            Assert.Equal("Manual", desktop.RuntimeReaderStatusText);
            Assert.Equal("0", desktop.TotalDeathsText);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentSaveCacheStaysSyncedWithoutFreshPublicationAndPendingLowerStillWins(bool zero)
    {
        PersistentTrackerState state = RuntimePublicationSessionTests.Selected(GameId.EldenRing);
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(state), new NullPublisher());
        var desktop = new DesktopTrackerViewModel(coordinator);
        await desktop.InitializeAsync();
        var session = new RuntimePublicationSession();
        int publications = 0;
        RuntimeGameReadResult Read(int total, int second) => RuntimeGameReadResult.Synced(new RuntimeGameObservation(
            GameId.EldenRing, total, DateTimeOffset.UtcNow.AddSeconds(second), EffectiveDeathTotalResult.SourceIdentityFor(state)));
        void Deliver(RuntimeGameReadResult result) => session.CompleteRead(session.BeginRead(state), state, result,
            desktop.ApplyRuntimeReaderResult, _ => publications++);
        RuntimeGameReadResult first = zero ? RuntimeGameReadResult.NoDeathsRecorded(Read(0, 0).Observation!) : Read(22, 0);
        Deliver(first);
        Assert.Equal("Synced", desktop.RuntimeReaderStatusText);
        for (int i = 0; i < 5; i++)
        {
            Deliver(RuntimeGameReadResult.Cached(first, isCurrentSaveCache: true));
            Assert.Equal("Synced", desktop.RuntimeReaderStatusText);
            Assert.Equal(zero ? DesktopTrackerViewModel.NoDeathsRecordedMessage : "22", desktop.TotalDeathsText);
            Assert.Equal(1, publications);
        }
        Deliver(RuntimeGameReadResult.Cached(first));
        Assert.Equal("Using last confirmed save data", desktop.RuntimeReaderStatusText);
        Deliver(Read(22, 1));
        Assert.Equal("Synced", desktop.RuntimeReaderStatusText);
        RuntimeGameReadResult lower = Read(0, 2);
        Deliver(lower);
        Deliver(RuntimeGameReadResult.Cached(lower, isCurrentSaveCache: true));
        Assert.Equal("Confirming lower death count", desktop.RuntimeReaderStatusText);
        Assert.Equal("22", desktop.TotalDeathsText);
        Assert.Equal(2, publications);
        Deliver(RuntimeGameReadResult.Unavailable(GameId.EldenRing, first.Observation));
        Assert.NotEqual("Synced", desktop.RuntimeReaderStatusText);
        Deliver(RuntimeGameReadResult.Cached(first, isCurrentSaveCache: true));
        Assert.NotEqual("Synced", desktop.RuntimeReaderStatusText);
    }

    [Fact]
    public async Task SuccessfulSaveSelectionKeepsIdentityWithoutTrackingCopy()
    {
        foreach (GameId game in new[] { GameId.EldenRing, GameId.BlackMythWukong })
        {
            var state = new PersistentTrackerState(PersistentTrackerState.CurrentSchemaVersion, game, OverlayConfiguration.Default, eldenRingNoticeAcknowledged: true);
            await using var coordinator = new SerializedTrackerCoordinator(new Repository(state), new NullPublisher());
            var desktop = new DesktopTrackerViewModel(coordinator, new Profiles(), null, null, null,
                (_, _) => Task.FromResult(new WukongSaveMetadataReadResult(true, new(12, null, null))));
            await desktop.InitializeAsync();
            var choice = new DiscoveredLocalSave(game == GameId.EldenRing ? "C:/synthetic/ER0000.sl2" : "C:/synthetic/ArchiveSaveFile.1.sav", "Save 1");
            if (game == GameId.EldenRing)
            {
                desktop.EldenRingSaveChoices.Add(choice);
                await desktop.SelectEldenRingSaveChoiceAsync(choice);
                Assert.Equal(choice, desktop.SelectedEldenRingSaveChoice);
                Assert.Equal(choice.Label, desktop.EldenRingSaveDiscoveryStatus);
                Assert.Single(desktop.EldenRingProfileSlots);
                desktop.ApplyRuntimeReaderResult(RuntimeGameReadResult.Unavailable(GameId.EldenRing));
                Assert.Equal("Game unavailable", desktop.RuntimeReaderStatusText);
            }
            else
            {
                desktop.BlackMythWukongSaveChoices.Add(choice);
                await desktop.SelectBlackMythWukongSaveChoiceAsync(choice);
                Assert.Equal(choice, desktop.SelectedBlackMythWukongSaveChoice);
                Assert.Equal(choice.Label, desktop.BlackMythWukongSaveDiscoveryStatus);
                Assert.Contains("12", desktop.BlackMythWukongSaveMetadataText);
            }
        }
    }

    private sealed class Profiles : IEldenRingSaveProfileReader
    {
        public ValueTask<IReadOnlyList<EldenRingCharacterSlotMetadata>> ReadAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<EldenRingCharacterSlotMetadata>>([new(0, false, "Synthetic", 12)]);
    }

    private sealed class Repository(PersistentTrackerState state) : ITrackerStateRepository
    {
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(state));
        public Task SaveAsync(PersistentTrackerState value, CancellationToken cancellationToken = default) { state = value; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
