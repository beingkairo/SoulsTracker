using SoulsTracker.Domain;
namespace SoulsTracker.Application;

public static class ConfirmedLegacyProposalApplication
{
    public static LegacyProposalApplicationResult Apply(LegacyStateAnalysis analysis, PersistentTrackerState destination)
    {
        ArgumentNullException.ThrowIfNull(analysis); ArgumentNullException.ThrowIfNull(destination);
        if (analysis.IsRejected || analysis.Proposal is null) return LegacyProposalApplicationResult.Refused(LegacyProposalApplicationOutcome.RejectedAnalysis);
        if (destination.SelectedGameId != GameId.DemonsSouls) return LegacyProposalApplicationResult.Refused(LegacyProposalApplicationOutcome.DestinationHasSelectedGame);
        if (destination.ManualDemonsSoulsDeathCounter.Value != 0) return LegacyProposalApplicationResult.Refused(LegacyProposalApplicationOutcome.DestinationHasManualDeaths);
        GameId? selected = analysis.Proposal.SelectedGameId;
        if (selected is null || selected == GameId.EldenRing || !GameCatalog.TryGet(selected.ToString(), out GameDefinition? game) || !game.IsSelectable) return LegacyProposalApplicationResult.Refused(LegacyProposalApplicationOutcome.InvalidProposal);
        return LegacyProposalApplicationResult.Applied(new PersistentTrackerState(destination.SchemaVersion, selected, destination.OverlayConfiguration, destination.GlobalHotkeys, destination.TextExports, ManualDeathCounter.CreateFor(GameId.DemonsSouls), destination.EldenRingNoticeAcknowledged, destination.EldenRingSave, destination.BlackMythWukongSave, destination.EldenRingMissedDeathAdjustments, destination.LiesOfPSave));
    }
}
public sealed class LegacyProposalApplicationResult(LegacyProposalApplicationOutcome outcome, PersistentTrackerState? candidateState)
{
    public LegacyProposalApplicationOutcome Outcome { get; } = outcome;
    public PersistentTrackerState? CandidateState { get; } = candidateState;
    internal static LegacyProposalApplicationResult Applied(PersistentTrackerState state) => new(LegacyProposalApplicationOutcome.Applied, state);
    internal static LegacyProposalApplicationResult Refused(LegacyProposalApplicationOutcome outcome) => new(outcome, null);
}
public enum LegacyProposalApplicationOutcome { Applied, RejectedAnalysis, DestinationHasSelectedGame, DestinationHasManualDeaths, InvalidProposal }
