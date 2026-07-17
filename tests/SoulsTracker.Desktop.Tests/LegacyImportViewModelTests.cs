using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class LegacyImportViewModelTests
{
    [Fact]
    public async Task FinalNonDefaultActionProjectsCommittedImportStateIntoDesktopViewModel()
    {
        var repository = new MemoryRepository();
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher(), new Committer());
        var tracker = new DesktopTrackerViewModel(coordinator);
        await tracker.InitializeAsync();
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze("{\"settings\":{\"selected_game\":\"ds3\"}}"u8.ToArray());
        var review = new LegacyImportPreflightReview(LegacyImportPreflightOutcome.Prepared, "Dark Souls III", [], null, 0, ConfirmedLegacyImportRequest.FromPreparedPreflight(analysis, new string('A', 64), new string('B', 64)));
        var candidate = new LegacyImportCandidate(LegacyImportSourceLabel.SoulsTrackerLegacySettings);
        var import = new LegacyImportViewModel(new LegacyImportWorkflow(new Locator(candidate), new Preflight(review), coordinator), tracker.ApplyImportedCommittedState);

        import.OfferIfEligible(PersistentTrackerState.Default);
        await import.ReviewAsync(candidate);
        await import.ImportReviewedSettingsAsync();

        Assert.Equal(GameId.Ds3, tracker.SelectedGame!.GameId);
        Assert.True(import.ReviewVisible);
        Assert.False(import.CanImport);
    }

    [Fact]
    public async Task DesktopProjectsLegacyPanelOnlyWhileAnOfferOrReviewIsActive()
    {
        var repository = new MemoryRepository();
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher(), new Committer());
        var tracker = new DesktopTrackerViewModel(coordinator);
        var candidate = new LegacyImportCandidate(LegacyImportSourceLabel.SoulsTrackerLegacySettings);
        var import = new LegacyImportViewModel(
            new LegacyImportWorkflow(new Locator(candidate), new Preflight(new LegacyImportPreflightReview(LegacyImportPreflightOutcome.Unavailable, null, [], null, 0, null)), coordinator),
            tracker.ApplyImportedCommittedState);

        tracker.ConfigureLegacyImport(import);
        Assert.False(tracker.HasActiveLegacyImport);

        import.OfferIfEligible(PersistentTrackerState.Default);
        Assert.True(tracker.HasActiveLegacyImport);

        import.Cancel();
        Assert.False(tracker.HasActiveLegacyImport);
    }

    private sealed class Locator(LegacyImportCandidate candidate) : IApprovedLegacyImportLocationLocator
    {
        public IReadOnlyList<LegacyImportCandidate> Discover() => [candidate];
        public bool IsStillApproved(LegacyImportCandidate value) => ReferenceEquals(value, candidate);
    }
    private sealed class Preflight(LegacyImportPreflightReview review) : IApprovedLegacyImportPreflight { public LegacyImportPreflightReview Prepare(LegacyImportCandidate candidate) => review; }
    private sealed class Committer : IConfirmedLegacyImportCommitter { public Task<SoulsTracker.Application.ConfirmedLegacyImportCommitOutcome> CommitAsync(LegacyProposalApplicationResult result, string source, string backup, CancellationToken cancellationToken = default) => Task.FromResult(SoulsTracker.Application.ConfirmedLegacyImportCommitOutcome.Committed); }
    private sealed class NullPublisher : ITrackerStateChangePublisher { public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class MemoryRepository : ITrackerStateRepository
    {
        public PersistentTrackerState State { get; private set; } = PersistentTrackerState.Default;
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(State));
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
