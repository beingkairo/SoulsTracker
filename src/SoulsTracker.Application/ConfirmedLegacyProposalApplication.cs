using SoulsTracker.Domain;

namespace SoulsTracker.Application;

/// <summary>
/// Applies an already confirmed, analyzed legacy proposal to an eligible
/// in-memory destination state. This type performs no persistence or I/O.
/// </summary>
public static class ConfirmedLegacyProposalApplication
{
    /// <summary>
    /// Produces a candidate state only when the supplied destination has no
    /// user-owned tracking content and the analysis contains a valid proposal.
    /// </summary>
    public static LegacyProposalApplicationResult Apply(
        LegacyStateAnalysis analysis,
        PersistentTrackerState destination)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(destination);

        if (analysis.IsRejected || analysis.Proposal is null)
        {
            return LegacyProposalApplicationResult.Refused(LegacyProposalApplicationOutcome.RejectedAnalysis);
        }

        if (destination.SelectedGameId is not null)
        {
            return LegacyProposalApplicationResult.Refused(LegacyProposalApplicationOutcome.DestinationHasSelectedGame);
        }

        if (HasDefeatedBosses(destination.BossProgress))
        {
            return LegacyProposalApplicationResult.Refused(LegacyProposalApplicationOutcome.DestinationHasDefeatedBossProgress);
        }

        if (destination.ManualBloodborneDeathCounter.Value != 0 || destination.ManualDemonsSoulsDeathCounter.Value != 0)
        {
            return LegacyProposalApplicationResult.Refused(LegacyProposalApplicationOutcome.DestinationHasManualBloodborneDeaths);
        }

        if (!TryCreateCandidate(analysis.Proposal, destination, out PersistentTrackerState? candidate))
        {
            return LegacyProposalApplicationResult.Refused(LegacyProposalApplicationOutcome.InvalidProposal);
        }

        return LegacyProposalApplicationResult.Applied(candidate!);
    }

    private static bool HasDefeatedBosses(BossProgress progress)
    {
        foreach (GameDefinition game in GameCatalog.All)
        {
            foreach (BossDefinition boss in game.BossCatalog)
            {
                if (progress.IsDefeated(game.Id, boss.Id))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryCreateCandidate(
        LegacyImportProposal proposal,
        PersistentTrackerState destination,
        out PersistentTrackerState? candidate)
    {
        candidate = null;

        if (!TryValidateSelectedGame(proposal.SelectedGameId) ||
            !TryValidateBosses(proposal.DefeatedBossesByGame) ||
            !TryValidateVisibilityMode(proposal.BossListVisibilityMode))
        {
            return false;
        }

        BossProgress progress = BossProgress.Empty;
        foreach ((GameId gameId, IReadOnlyList<BossId> bossIds) in proposal.DefeatedBossesByGame)
        {
            foreach (BossId bossId in bossIds)
            {
                progress = progress.MarkDefeated(gameId, bossId);
            }
        }

        OverlayConfiguration existingOverlay = destination.OverlayConfiguration;
        BossListOverlayOptions bossList = proposal.BossListVisibilityMode is BossListVisibilityMode visibilityMode
            ? new BossListOverlayOptions(existingOverlay.BossList.IsEnabled, visibilityMode)
            : existingOverlay.BossList;
        OverlayConfiguration overlay = ReferenceEquals(bossList, existingOverlay.BossList)
            ? existingOverlay
            : new OverlayConfiguration(
                existingOverlay.SchemaVersion,
                existingOverlay.Endpoint,
                existingOverlay.TotalDeaths,
                bossList);

        candidate = new PersistentTrackerState(
            destination.SchemaVersion,
            proposal.SelectedGameId,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            progress,
            overlay,
            destination.ManualBloodborneHotkeys,
            destination.DeathSound, destination.TextExports, ManualBloodborneDeathCounter.CreateFor(GameId.DemonsSouls), destination.EldenRingNoticeAcknowledged, destination.EldenRingSave);
        return true;
    }

    private static bool TryValidateSelectedGame(GameId? selectedGameId)
    {
        if (selectedGameId is null)
        {
            return true;
        }

        return selectedGameId != GameId.EldenRing &&
            GameCatalog.TryGet(selectedGameId.Value, out GameDefinition? game) && game.IsSelectable;
    }

    private static bool TryValidateBosses(IReadOnlyDictionary<GameId, IReadOnlyList<BossId>> defeatedBossesByGame)
    {
        foreach ((GameId gameId, IReadOnlyList<BossId> bossIds) in defeatedBossesByGame)
        {
            if (gameId is null ||
                bossIds is null ||
                !GameCatalog.TryGet(gameId.Value, out GameDefinition? game) ||
                !game.IsSelectable)
            {
                return false;
            }

            foreach (BossId bossId in bossIds)
            {
                if (bossId is null || !game.BossCatalog.Any(knownBoss => knownBoss.Id == bossId))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryValidateVisibilityMode(BossListVisibilityMode? visibilityMode) =>
        visibilityMode is null || Enum.IsDefined(visibilityMode.Value);
}

/// <summary>
/// Describes the safe, in-memory outcome of applying a confirmed legacy proposal.
/// </summary>
public sealed class LegacyProposalApplicationResult
{
    private LegacyProposalApplicationResult(
        LegacyProposalApplicationOutcome outcome,
        PersistentTrackerState? candidateState)
    {
        Outcome = outcome;
        CandidateState = candidateState;
    }

    /// <summary>
    /// Gets the safe outcome category without filesystem, import-source, or exception detail.
    /// </summary>
    public LegacyProposalApplicationOutcome Outcome { get; }

    /// <summary>
    /// Gets the candidate state only when <see cref="Outcome"/> is <see cref="LegacyProposalApplicationOutcome.Applied"/>.
    /// </summary>
    public PersistentTrackerState? CandidateState { get; }

    internal static LegacyProposalApplicationResult Applied(PersistentTrackerState candidateState) =>
        new(LegacyProposalApplicationOutcome.Applied, candidateState ?? throw new ArgumentNullException(nameof(candidateState)));

    internal static LegacyProposalApplicationResult Refused(LegacyProposalApplicationOutcome outcome) =>
        new(outcome, candidateState: null);
}

/// <summary>
/// Enumerates application outcomes without disclosing a legacy source or error detail.
/// </summary>
public enum LegacyProposalApplicationOutcome
{
    Applied,
    RejectedAnalysis,
    DestinationHasSelectedGame,
    DestinationHasDefeatedBossProgress,
    DestinationHasManualBloodborneDeaths,
    InvalidProposal,
}
