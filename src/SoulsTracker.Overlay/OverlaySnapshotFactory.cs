using SoulsTracker.Domain;

namespace SoulsTracker.Overlay;

/// <summary>Creates the browser-safe snapshot shared by the in-app and sign-in hosts.</summary>
public static class OverlaySnapshotFactory
{
    public static OverlaySnapshot Create(PersistentTrackerState state, RuntimeGameObservation? observation, long sequenceNumber)
    {
        ArgumentNullException.ThrowIfNull(state);
        OverlayGameMetadata game = new(state.SelectedGameId);
        bool usable = EffectiveDeathTotalResult.Resolve(state, observation).Status is not EffectiveDeathTotalStatus.SourceMismatch && observation?.GameId == state.SelectedGameId &&
            (state.SelectedGameId != GameId.BlackMythWukong || state.BlackMythWukongSave.LocalPath is not null) &&
            (state.SelectedGameId != GameId.LiesOfP || state.LiesOfPSave.LocalPath is not null);
        TotalDeathsDisplayValue deaths = usable
            ? TotalDeathsDisplayValue.FromRuntimeObservation(observation!)
            : GameCatalog.GetRequired(state.SelectedGameId).TrackingMode == GameTrackingMode.ManualOnly
                ? TotalDeathsDisplayValue.FromManualCounter(state.SelectedGameId, state.GetManualDeathCounter(state.SelectedGameId))
                : state.SelectedGameId == GameId.BlackMythWukong || state.SelectedGameId == GameId.LiesOfP
                    ? TotalDeathsDisplayValue.UnavailableForSelectedGame(state.SelectedGameId)
                    : TotalDeathsDisplayValue.FromUnavailableSelectedGame(state.SelectedGameId);
        long? combinedTotal = EffectiveDeathTotalResult.Resolve(state, usable ? observation : null).EffectiveDisplayedTotal;
        if (combinedTotal.HasValue && deaths.GameId == GameId.EldenRing) deaths = TotalDeathsDisplayValue.WithNumericValue(deaths, combinedTotal.Value);
        return new OverlaySnapshot(OverlaySnapshot.CurrentSchemaVersion, sequenceNumber, DateTimeOffset.UtcNow, game, deaths, OverlayPresentationConfiguration.From(state.OverlayConfiguration));
    }

    public static bool CanUseRuntimeObservation(PersistentTrackerState state, RuntimeGameObservation? observation) =>
        observation?.GameId == state.SelectedGameId &&
        EffectiveDeathTotalResult.Resolve(state, observation).Status is not EffectiveDeathTotalStatus.SourceMismatch &&
        (state.SelectedGameId != GameId.BlackMythWukong || state.BlackMythWukongSave.LocalPath is not null) &&
        (state.SelectedGameId != GameId.LiesOfP || state.LiesOfPSave.LocalPath is not null);
}
