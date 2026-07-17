using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Owns the passive, two-confirmation desktop migration flow without receiving source paths.</summary>
public sealed class LegacyImportWorkflow
{
    private readonly IApprovedLegacyImportLocationLocator locator;
    private readonly IApprovedLegacyImportPreflight preflight;
    private readonly SerializedTrackerCoordinator coordinator;
    private IReadOnlyList<LegacyImportCandidate> candidates = [];
    private LegacyImportPreflightReview? prepared;
    private bool reviewAttempted;
    private bool reviewRendered;
    private bool finished;

    public LegacyImportWorkflow(IApprovedLegacyImportLocationLocator locator, IApprovedLegacyImportPreflight preflight, SerializedTrackerCoordinator coordinator)
    {
        this.locator = locator ?? throw new ArgumentNullException(nameof(locator));
        this.preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    /// <summary>Performs only metadata discovery after normal state load confirms the destination is empty.</summary>
    public IReadOnlyList<LegacyImportCandidate> OfferIfEligible(PersistentTrackerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (finished || !IsEligible(state)) return [];
        candidates = locator.Discover();
        return candidates;
    }

    /// <summary>First deliberate action: preflights one selected opaque candidate without retries.</summary>
    public Task<LegacyImportReviewResult> ReviewAsync(LegacyImportCandidate? selectedCandidate, CancellationToken cancellationToken = default)
    {
        if (finished || reviewAttempted || candidates.Count == 0) return Task.FromResult(LegacyImportReviewResult.Unavailable());
        LegacyImportCandidate? selected = candidates.Count == 1 ? candidates[0] : selectedCandidate;
        if (selected is null || !candidates.Contains(selected)) return Task.FromResult(LegacyImportReviewResult.SelectionRequired());
        reviewAttempted = true;
        prepared = preflight.Prepare(selected);
        return Task.FromResult(CreateReviewResult(selected, prepared));
    }

    /// <summary>Enables the separate final action only after a prepared review has been rendered.</summary>
    public void MarkReviewRendered() => reviewRendered = prepared?.Outcome == LegacyImportPreflightOutcome.Prepared;

    /// <summary>Second deliberate action: submits the reviewed analysis to the serialized coordinator.</summary>
    public async Task<ConfirmedLegacyImportExecutionResult> ImportReviewedSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!reviewRendered || prepared is not { Outcome: LegacyImportPreflightOutcome.Prepared } || !prepared.TryGetConfirmedRequest(out ConfirmedLegacyImportRequest? request) || request is null)
        {
            return new(ConfirmedLegacyImportExecutionStatus.Refused, null, "The reviewed import is not available.");
        }

        ConfirmedLegacyImportExecutionResult result = await coordinator.ImportReviewedLegacySettingsAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Status == ConfirmedLegacyImportExecutionStatus.Committed) finished = true;
        return result;
    }

    /// <summary>Refusal is final for this process and intentionally preserves any prepared backup.</summary>
    public void Cancel() => finished = true;

    private static bool IsEligible(PersistentTrackerState state) =>
        state.SelectedGameId is null &&
        state.ManualBloodborneDeathCounter.Value == 0 &&
        state.ManualDemonsSoulsDeathCounter.Value == 0 &&
        !GameCatalog.All.Any(game => game.BossCatalog.Any(boss => state.BossProgress.IsDefeated(game.Id, boss.Id)));

    private static LegacyImportReviewResult CreateReviewResult(LegacyImportCandidate candidate, LegacyImportPreflightReview result)
    {
        if (result.Outcome != LegacyImportPreflightOutcome.Prepared) return new(result.Outcome, candidate.DisplayLabel, null, [], null, 0, false);
        return new(result.Outcome, candidate.DisplayLabel, result.SelectedGameLabel, result.RecognizedBossNames, result.BossListVisibilityMode, result.WarningCount, true);
    }
}

/// <summary>Safe, displayable review data; no source location, fingerprints, raw JSON, or exception data.</summary>
public sealed record LegacyImportReviewResult(
    LegacyImportPreflightOutcome Outcome,
    string? SourceLabel,
    string? SelectedGameLabel,
    IReadOnlyList<string> RecognizedBossNames,
    BossListVisibilityMode? BossListVisibilityMode,
    int WarningCount,
    bool BackupCreated)
{
    internal static LegacyImportReviewResult Unavailable() => new(LegacyImportPreflightOutcome.Unavailable, null, null, [], null, 0, false);
    internal static LegacyImportReviewResult SelectionRequired() => new(LegacyImportPreflightOutcome.Unavailable, null, null, [], null, 0, false);
}
