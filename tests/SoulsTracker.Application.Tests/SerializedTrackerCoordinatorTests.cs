using SoulsTracker.Domain;

namespace SoulsTracker.Application.Tests;

public sealed class SerializedTrackerCoordinatorTests
{
    [Fact]
    public async Task CommandsAreSerializedAndNoOpsDoNotSaveOrPublish()
    {
        var repository = new MemoryRepository(PersistentTrackerState.Default);
        var publisher = new RecordingPublisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        Assert.True((await coordinator.InitializeAsync()).IsSuccess);
        await coordinator.SubmitAsync(new SelectGameCommand(GameId.Bloodborne));
        Task<TrackerCommandExecutionResult>[] commands = Enumerable.Range(0, 100).Select(_ => coordinator.SubmitAsync(new IncrementManualBloodborneDeathsCommand())).ToArray();
        TrackerCommandExecutionResult[] results = await Task.WhenAll(commands);
        TrackerCommandExecutionResult noOp = await coordinator.SubmitAsync(new SelectGameCommand(GameId.Bloodborne));
        Assert.Equal(100, results.Last().CommittedState!.ManualBloodborneDeathCounter.Value);
        Assert.Equal(101, repository.Saves); Assert.Equal(101, publisher.Published); Assert.Equal(TrackerCommandExecutionStatus.NoChange, noOp.Status);
    }

    [Fact]
    public async Task SaveFailureDoesNotChangeCommittedStateOrPublishAndMessagesAreRedacted()
    {
        var repository = new MemoryRepository(PersistentTrackerState.Default) { FailSaves = true };
        var publisher = new RecordingPublisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        await coordinator.InitializeAsync();
        TrackerCommandExecutionResult result = await coordinator.SubmitAsync(new SelectGameCommand(GameId.Bloodborne));
        Assert.Equal(TrackerCommandExecutionStatus.SaveFailed, result.Status); Assert.Null(result.CommittedState!.SelectedGameId); Assert.Equal(0, publisher.Published); Assert.DoesNotContain("secret", result.FailureMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublisherFailureKeepsCommittedState()
    {
        var repository = new MemoryRepository(PersistentTrackerState.Default);
        var publisher = new RecordingPublisher { Fail = true };
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        await coordinator.InitializeAsync();
        TrackerCommandExecutionResult result = await coordinator.SubmitAsync(new SelectGameCommand(GameId.Bloodborne));
        Assert.Equal(TrackerCommandExecutionStatus.DeliveryFailed, result.Status); Assert.Equal(GameId.Bloodborne, result.CommittedState!.SelectedGameId); Assert.Equal(GameId.Bloodborne, repository.State.SelectedGameId);
    }

    [Fact]
    public async Task PresentationCommandsUseTheSameSerializedSaveAndPublishPath()
    {
        var repository = new MemoryRepository(PersistentTrackerState.Default);
        var publisher = new RecordingPublisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        await coordinator.InitializeAsync();

        BossListVisibilityMode[] modes = [BossListVisibilityMode.Remaining, BossListVisibilityMode.Defeated, BossListVisibilityMode.All];
        Task<TrackerCommandExecutionResult>[] requests = Enumerable.Range(0, 99)
            .Select(index => coordinator.SubmitAsync(new UpdateOverlayPresentationCommand(
                true,
                true,
                true,
                modes[index % modes.Length])))
            .ToArray();
        TrackerCommandExecutionResult[] results = await Task.WhenAll(requests);

        Assert.All(results, result => Assert.Equal(TrackerCommandExecutionStatus.Applied, result.Status));
        Assert.Equal(99, repository.Saves);
        Assert.Equal(99, publisher.Published);
        Assert.Equal(BossListVisibilityMode.All, repository.State.OverlayConfiguration.BossList.VisibilityMode);
    }

    [Fact]
    public async Task TextExportConfigurationChangesPreserveTheIndependentDemonsSoulsCounter()
    {
        PersistentTrackerState initial = new(
            PersistentTrackerState.CurrentSchemaVersion,
            GameId.DemonsSouls,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 3),
            BossProgress.Empty,
            OverlayConfiguration.Default,
            manualDemonsSoulsDeathCounter: ManualBloodborneDeathCounter.CreateFor(GameId.DemonsSouls, 11));
        var repository = new MemoryRepository(initial);
        var publisher = new RecordingPublisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        Assert.True((await coordinator.InitializeAsync()).IsSuccess);

        PersistentTrackerState updated = await coordinator.SetTextExportConfigurationAsync(
            new TextExportConfiguration("C:\\exports\\deaths.txt", true, null, false));

        Assert.Equal(GameId.DemonsSouls, updated.SelectedGameId);
        Assert.Equal(3, updated.ManualBloodborneDeathCounter.Value);
        Assert.Equal(11, updated.ManualDemonsSoulsDeathCounter.Value);
        Assert.Equal(1, publisher.Published);
    }

    [Fact]
    public async Task ReviewedImportUsesLatestQueuedStateAndRefusesWithoutCommitWhenDestinationChanged()
    {
        var repository = new MemoryRepository(PersistentTrackerState.Default);
        var committer = new RecordingImportCommitter();
        await using var coordinator = new SerializedTrackerCoordinator(repository, new RecordingPublisher(), committer);
        await coordinator.InitializeAsync();
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze("{\"settings\":{\"selected_game\":\"ds3\"}}"u8.ToArray());
        ConfirmedLegacyImportRequest request = ConfirmedLegacyImportRequest.FromPreparedPreflight(analysis, new string('A', 64), new string('B', 64));

        await coordinator.SubmitAsync(new SelectGameCommand(GameId.Bloodborne));
        ConfirmedLegacyImportExecutionResult result = await coordinator.ImportReviewedLegacySettingsAsync(request);

        Assert.Equal(ConfirmedLegacyImportExecutionStatus.Refused, result.Status);
        Assert.Equal(0, committer.Calls);
        Assert.Equal(GameId.Bloodborne, repository.State.SelectedGameId);
    }

    [Fact]
    public async Task CommittedReviewedImportUpdatesCoordinatorBeforeLaterCommandsAndPublishes()
    {
        var repository = new MemoryRepository(PersistentTrackerState.Default);
        var publisher = new RecordingPublisher();
        var committer = new RecordingImportCommitter();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher, committer);
        await coordinator.InitializeAsync();
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze("{\"settings\":{\"selected_game\":\"ds3\"}}"u8.ToArray());
        ConfirmedLegacyImportRequest request = ConfirmedLegacyImportRequest.FromPreparedPreflight(analysis, new string('A', 64), new string('B', 64));

        ConfirmedLegacyImportExecutionResult imported = await coordinator.ImportReviewedLegacySettingsAsync(request);
        TrackerCommandExecutionResult later = await coordinator.SubmitAsync(new SetBossDefeatedCommand(GameId.Ds3, GameCatalog.GetRequired(GameId.Ds3).BossCatalog[0].Id, true));

        Assert.Equal(ConfirmedLegacyImportExecutionStatus.Committed, imported.Status);
        Assert.Equal(GameId.Ds3, later.CommittedState!.SelectedGameId);
        Assert.True(later.CommittedState.BossProgress.IsDefeated(GameId.Ds3, GameCatalog.GetRequired(GameId.Ds3).BossCatalog[0].Id));
        Assert.Equal(1, committer.Calls);
        Assert.Equal(2, publisher.Published);
    }

    private sealed class MemoryRepository(PersistentTrackerState state) : ITrackerStateRepository
    {
        private PersistentTrackerState state = state;
        public PersistentTrackerState State => state;
        public bool FailSaves { get; init; }
        public int Saves { get; private set; }
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(state));
        public Task SaveAsync(PersistentTrackerState newState, CancellationToken cancellationToken = default) { if (FailSaves) throw new InvalidOperationException("secret"); state = newState; Saves++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RecordingPublisher : ITrackerStateChangePublisher { public int Published { get; private set; } public bool Fail { get; init; } public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) { Published++; if (Fail) throw new InvalidOperationException("secret"); return Task.CompletedTask; } }
    private sealed class RecordingImportCommitter : IConfirmedLegacyImportCommitter
    {
        public int Calls { get; private set; }
        public Task<ConfirmedLegacyImportCommitOutcome> CommitAsync(LegacyProposalApplicationResult applicationResult, string sourceFingerprint, string backupFingerprint, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(ConfirmedLegacyImportCommitOutcome.Committed);
        }
    }
}
