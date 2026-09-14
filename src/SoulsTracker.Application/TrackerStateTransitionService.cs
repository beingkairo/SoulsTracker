using SoulsTracker.Domain;

namespace SoulsTracker.Application;

/// <summary>
/// Applies the approved V1 tracker commands as pure, synchronous immutable-state transitions.
/// </summary>
public static class TrackerStateTransitionService
{
    /// <summary>
    /// Evaluates one approved command against immutable persistent state.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the command or its values are invalid for the state.</exception>
    public static TrackerTransitionResult Apply(PersistentTrackerState state, ITrackerCommand command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            SelectGameCommand selectGame => ApplySelectGame(state, selectGame),
            IncrementManualBloodborneDeathsCommand increment => ApplyIncrementManualDeaths(state, increment),
            DecrementManualBloodborneDeathsCommand decrement => ApplyDecrementManualDeaths(state, decrement),
            UpdateOverlayPresentationCommand updatePresentation => ApplyUpdateOverlayPresentation(state, updatePresentation),
            ResetOverlayAppearanceCommand resetAppearance => ResetOverlayAppearance(state, resetAppearance),
            UpdateOverlayAppearanceCommand updateAppearance => ApplyUpdateOverlayAppearance(state, updateAppearance),
            AcknowledgeEldenRingNoticeCommand => ApplyAcknowledgeEldenRingNotice(state),
            UpdateEldenRingSaveConfigurationCommand updateEldenRingSave => ApplyUpdateEldenRingSaveConfiguration(state, updateEldenRingSave),
            AdjustEldenRingMissedDeathsCommand adjustEldenRingMissedDeaths => ApplyAdjustEldenRingMissedDeaths(state, adjustEldenRingMissedDeaths),
            UpdateBlackMythWukongSaveConfigurationCommand updateBlackMythWukongSave => ApplyUpdateBlackMythWukongSaveConfiguration(state, updateBlackMythWukongSave),
            UpdateLiesOfPSaveConfigurationCommand updateLiesOfPSave => ApplyUpdateLiesOfPSaveConfiguration(state, updateLiesOfPSave),
            _ => throw new ArgumentException("The tracker command is not supported.", nameof(command)),
        };
    }

    private static TrackerTransitionResult ApplySelectGame(PersistentTrackerState state, SelectGameCommand command)
    {
        GameId gameId = RequireSelectableGame(command.GameId, nameof(command));
        if (gameId == GameId.EldenRing && !state.EldenRingNoticeAcknowledged)
        {
            throw new ArgumentException("Elden Ring requires local acknowledgement before selection.", nameof(command));
        }
        if (state.SelectedGameId == gameId)
        {
            return Unchanged(state, TrackerCommandType.SelectGame);
        }

        return Changed(
            new PersistentTrackerState(
                state.SchemaVersion,
                gameId,
                state.ManualBloodborneDeathCounter,
                state.OverlayConfiguration, state.ManualBloodborneHotkeys, state.TextExports, state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave, state.BlackMythWukongSave, state.EldenRingMissedDeathAdjustments, state.LiesOfPSave),
            TrackerCommandType.SelectGame);
    }

    private static TrackerTransitionResult ApplyIncrementManualDeaths(
        PersistentTrackerState state,
        IncrementManualBloodborneDeathsCommand _)
    {
        RequireManualGameSelected(state);
        return Changed(
            new PersistentTrackerState(
                state.SchemaVersion,
                state.SelectedGameId,
                state.SelectedGameId == GameId.Bloodborne ? state.ManualBloodborneDeathCounter.Increment() : state.ManualBloodborneDeathCounter,
                state.OverlayConfiguration, state.ManualBloodborneHotkeys, state.TextExports,
                state.SelectedGameId == GameId.DemonsSouls ? state.ManualDemonsSoulsDeathCounter.Increment() : state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave,
                state.BlackMythWukongSave, state.EldenRingMissedDeathAdjustments, state.LiesOfPSave),
            TrackerCommandType.IncrementManualBloodborneDeaths);
    }

    private static TrackerTransitionResult ApplyDecrementManualDeaths(
        PersistentTrackerState state,
        DecrementManualBloodborneDeathsCommand _)
    {
        RequireManualGameSelected(state);
        ManualBloodborneDeathCounter currentCounter = state.GetManualDeathCounter(state.SelectedGameId!);
        ManualBloodborneDeathCounter updatedCounter = currentCounter.Decrement();
        if (ReferenceEquals(updatedCounter, currentCounter))
        {
            return Unchanged(state, TrackerCommandType.DecrementManualBloodborneDeaths);
        }

        return Changed(
            new PersistentTrackerState(
                state.SchemaVersion,
                state.SelectedGameId,
                state.SelectedGameId == GameId.Bloodborne ? updatedCounter : state.ManualBloodborneDeathCounter,
                state.OverlayConfiguration, state.ManualBloodborneHotkeys, state.TextExports,
                state.SelectedGameId == GameId.DemonsSouls ? updatedCounter : state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave, 
                state.BlackMythWukongSave, state.EldenRingMissedDeathAdjustments, state.LiesOfPSave),
            TrackerCommandType.DecrementManualBloodborneDeaths);
    }

    private static TrackerTransitionResult ApplyUpdateOverlayPresentation(
        PersistentTrackerState state,
        UpdateOverlayPresentationCommand command)
    {
        OverlayConfiguration existing = state.OverlayConfiguration;
        var updatedConfiguration = new OverlayConfiguration(
            existing.SchemaVersion,
            existing.Endpoint,
            new TotalDeathsOverlayOptions(command.IsTotalDeathsEnabled, command.ShowGameName, existing.TotalDeaths.CompactTitle, existing.TotalDeaths.Appearance, existing.TotalDeaths.TitleIconMode));

        if (PresentationEquals(existing, updatedConfiguration))
        {
            return Unchanged(state, TrackerCommandType.UpdateOverlayPresentation);
        }

        return Changed(
            new PersistentTrackerState(
                state.SchemaVersion,
                state.SelectedGameId,
                state.ManualBloodborneDeathCounter,
                updatedConfiguration, state.ManualBloodborneHotkeys, state.TextExports, state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave, state.BlackMythWukongSave, state.EldenRingMissedDeathAdjustments, state.LiesOfPSave),
            TrackerCommandType.UpdateOverlayPresentation);
    }

    private static TrackerTransitionResult ResetOverlayAppearance(PersistentTrackerState state, ResetOverlayAppearanceCommand command)
    {
        OverlayConfiguration existing = state.OverlayConfiguration;
        OverlayConfiguration updated = new OverlayConfiguration(existing.SchemaVersion, existing.Endpoint,
            new TotalDeathsOverlayOptions(existing.TotalDeaths.IsEnabled, existing.TotalDeaths.ShowGameName, existing.TotalDeaths.CompactTitle, OverlayAppearance.Default, existing.TotalDeaths.TitleIconMode));
        return Changed(new PersistentTrackerState(state.SchemaVersion, state.SelectedGameId, state.ManualBloodborneDeathCounter, updated, state.ManualBloodborneHotkeys, state.TextExports, state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave, state.BlackMythWukongSave, state.EldenRingMissedDeathAdjustments, state.LiesOfPSave), TrackerCommandType.ResetOverlayAppearance);
    }

    private static TrackerTransitionResult ApplyUpdateOverlayAppearance(PersistentTrackerState state, UpdateOverlayAppearanceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command.Appearance);
        OverlayConfiguration existing = state.OverlayConfiguration;
        OverlayConfiguration updated = new OverlayConfiguration(existing.SchemaVersion, existing.Endpoint,
            new TotalDeathsOverlayOptions(existing.TotalDeaths.IsEnabled, command.TotalDeathsShowGameName, command.TotalDeathsCompactTitle, command.Appearance, command.TotalDeathsTitleIconMode));
        return Changed(new PersistentTrackerState(state.SchemaVersion, state.SelectedGameId, state.ManualBloodborneDeathCounter, updated, state.ManualBloodborneHotkeys, state.TextExports, state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave, state.BlackMythWukongSave, state.EldenRingMissedDeathAdjustments, state.LiesOfPSave), TrackerCommandType.UpdateOverlayAppearance);
    }


    private static TrackerTransitionResult ApplyAcknowledgeEldenRingNotice(PersistentTrackerState state) =>
        state.EldenRingNoticeAcknowledged
            ? Unchanged(state, TrackerCommandType.AcknowledgeEldenRingNotice)
            : Changed(
                new PersistentTrackerState(
                    state.SchemaVersion,
                    state.SelectedGameId,
                    state.ManualBloodborneDeathCounter,
                        state.OverlayConfiguration,
                    state.ManualBloodborneHotkeys,

                    state.TextExports,
                    state.ManualDemonsSoulsDeathCounter,
                    eldenRingNoticeAcknowledged: true,
                    state.EldenRingSave, state.BlackMythWukongSave, state.EldenRingMissedDeathAdjustments, state.LiesOfPSave),
                TrackerCommandType.AcknowledgeEldenRingNotice);

    private static TrackerTransitionResult ApplyUpdateEldenRingSaveConfiguration(PersistentTrackerState state, UpdateEldenRingSaveConfigurationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command.Configuration);
        if (state.EldenRingSave == command.Configuration)
        {
            return Unchanged(state, TrackerCommandType.UpdateEldenRingSaveConfiguration);
        }

        return Changed(
            new PersistentTrackerState(
                state.SchemaVersion,
                state.SelectedGameId,
                state.ManualBloodborneDeathCounter,
                state.OverlayConfiguration,
                state.ManualBloodborneHotkeys,

                state.TextExports,
                state.ManualDemonsSoulsDeathCounter,
                state.EldenRingNoticeAcknowledged,
                command.Configuration, state.BlackMythWukongSave, state.EldenRingMissedDeathAdjustments, state.LiesOfPSave),
            TrackerCommandType.UpdateEldenRingSaveConfiguration);
    }

    private static TrackerTransitionResult ApplyUpdateBlackMythWukongSaveConfiguration(PersistentTrackerState state, UpdateBlackMythWukongSaveConfigurationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command.Configuration);
        if (state.BlackMythWukongSave == command.Configuration)
        {
            return Unchanged(state, TrackerCommandType.UpdateBlackMythWukongSaveConfiguration);
        }

        return Changed(
            new PersistentTrackerState(
                state.SchemaVersion,
                state.SelectedGameId,
                state.ManualBloodborneDeathCounter,
                state.OverlayConfiguration,
                state.ManualBloodborneHotkeys,

                state.TextExports,
                state.ManualDemonsSoulsDeathCounter,
                state.EldenRingNoticeAcknowledged,
                state.EldenRingSave,

                command.Configuration,
                state.EldenRingMissedDeathAdjustments, state.LiesOfPSave),
            TrackerCommandType.UpdateBlackMythWukongSaveConfiguration);
    }

    private static TrackerTransitionResult ApplyUpdateLiesOfPSaveConfiguration(PersistentTrackerState state, UpdateLiesOfPSaveConfigurationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command.Configuration);
        if (state.LiesOfPSave == command.Configuration) return Unchanged(state, TrackerCommandType.UpdateLiesOfPSaveConfiguration);
        return Changed(new PersistentTrackerState(
            state.SchemaVersion, state.SelectedGameId, state.ManualBloodborneDeathCounter, 
            state.OverlayConfiguration, state.ManualBloodborneHotkeys, state.TextExports,
            state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave,
            state.BlackMythWukongSave, state.EldenRingMissedDeathAdjustments,
            command.Configuration), TrackerCommandType.UpdateLiesOfPSaveConfiguration);
    }

    private static TrackerTransitionResult ApplyAdjustEldenRingMissedDeaths(PersistentTrackerState state, AdjustEldenRingMissedDeathsCommand command)
    {
        if (state.SelectedGameId != GameId.EldenRing || !EldenRingMissedDeathAdjustments.IsConfiguredCharacter(state.EldenRingSave))
        {
            throw new ArgumentException("Elden Ring missed deaths require a selected local save and character.", nameof(state));
        }

        EldenRingMissedDeathAdjustments updated = command.Increment
            ? state.EldenRingMissedDeathAdjustments.Increment(state.EldenRingSave)
            : state.EldenRingMissedDeathAdjustments.Decrement(state.EldenRingSave);
        if (ReferenceEquals(updated, state.EldenRingMissedDeathAdjustments))
        {
            return Unchanged(state, TrackerCommandType.AdjustEldenRingMissedDeaths);
        }

        return Changed(new PersistentTrackerState(
            state.SchemaVersion, state.SelectedGameId, state.ManualBloodborneDeathCounter, 
            state.OverlayConfiguration, state.ManualBloodborneHotkeys, state.TextExports,
            state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave,
            state.BlackMythWukongSave, updated, state.LiesOfPSave), TrackerCommandType.AdjustEldenRingMissedDeaths);
    }

    private static bool PresentationEquals(OverlayConfiguration left, OverlayConfiguration right) =>
        left.TotalDeaths.IsEnabled == right.TotalDeaths.IsEnabled &&
        left.TotalDeaths.ShowGameName == right.TotalDeaths.ShowGameName &&
        true;

    private static GameId RequireSelectableGame(GameId? gameId, string parameterName)
    {
        if (gameId is null)
        {
            throw new ArgumentException("The game ID is required.", parameterName);
        }
        GameDefinition definition = GameCatalog.GetRequired(gameId);
        if (!definition.IsSelectable)
        {
            throw new ArgumentException("A disabled SOON game cannot be selected or updated.", parameterName);
        }

        return gameId;
    }

    private static void RequireManualGameSelected(PersistentTrackerState state)
    {
        if (state.SelectedGameId != GameId.Bloodborne && state.SelectedGameId != GameId.DemonsSouls)
        {
            throw new ArgumentException("Manual death commands require Bloodborne or Demon Souls to be selected.", nameof(state));
        }
    }

    private static TrackerTransitionResult Changed(PersistentTrackerState state, TrackerCommandType commandType) =>
        new(state, stateChanged: true, commandType);

    private static TrackerTransitionResult Unchanged(PersistentTrackerState state, TrackerCommandType commandType) =>
        new(state, stateChanged: false, commandType);
}
