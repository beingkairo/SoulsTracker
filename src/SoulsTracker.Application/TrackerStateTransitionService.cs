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
            SetBossDefeatedCommand setBossDefeated => ApplySetBossDefeated(state, setBossDefeated),
            UpdateOverlayPresentationCommand updatePresentation => ApplyUpdateOverlayPresentation(state, updatePresentation),
            ResetOverlayAppearanceCommand resetAppearance => ResetOverlayAppearance(state, resetAppearance),
            UpdateOverlayAppearanceCommand updateAppearance => ApplyUpdateOverlayAppearance(state, updateAppearance),
            UpdateDeathSoundConfigurationCommand updateDeathSound => ApplyUpdateDeathSoundConfiguration(state, updateDeathSound),
            AcknowledgeEldenRingNoticeCommand => ApplyAcknowledgeEldenRingNotice(state),
            UpdateEldenRingSaveConfigurationCommand updateEldenRingSave => ApplyUpdateEldenRingSaveConfiguration(state, updateEldenRingSave),
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
                state.BossProgress,
                state.OverlayConfiguration, state.ManualBloodborneHotkeys, state.DeathSound, state.TextExports, state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave),
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
                state.SelectedGameId == GameId.DemonsSouls ? state.ManualBloodborneDeathCounter : state.ManualBloodborneDeathCounter.Increment(),
                state.BossProgress,
                state.OverlayConfiguration, state.ManualBloodborneHotkeys, state.DeathSound, state.TextExports,
                state.SelectedGameId == GameId.DemonsSouls ? state.ManualDemonsSoulsDeathCounter.Increment() : state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave),
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
                state.SelectedGameId == GameId.DemonsSouls ? state.ManualBloodborneDeathCounter : updatedCounter,
                state.BossProgress,
                state.OverlayConfiguration, state.ManualBloodborneHotkeys, state.DeathSound, state.TextExports,
                state.SelectedGameId == GameId.DemonsSouls ? updatedCounter : state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave),
            TrackerCommandType.DecrementManualBloodborneDeaths);
    }

    private static TrackerTransitionResult ApplySetBossDefeated(
        PersistentTrackerState state,
        SetBossDefeatedCommand command)
    {
        GameId gameId = RequireSelectableGame(command.GameId, nameof(command));
        if (command.BossId is null)
        {
            throw new ArgumentException("The boss ID is required.", nameof(command));
        }
        GameCatalog.GetRequiredBoss(gameId, command.BossId);

        BossProgress updatedProgress = command.IsDefeated
            ? state.BossProgress.MarkDefeated(gameId, command.BossId)
            : state.BossProgress.ClearDefeated(gameId, command.BossId);

        if (ReferenceEquals(updatedProgress, state.BossProgress))
        {
            return Unchanged(state, TrackerCommandType.SetBossDefeated);
        }

        return Changed(
            new PersistentTrackerState(
                state.SchemaVersion,
                state.SelectedGameId,
                state.ManualBloodborneDeathCounter,
                updatedProgress,
                state.OverlayConfiguration, state.ManualBloodborneHotkeys, state.DeathSound, state.TextExports, state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave),
            TrackerCommandType.SetBossDefeated);
    }

    private static TrackerTransitionResult ApplyUpdateOverlayPresentation(
        PersistentTrackerState state,
        UpdateOverlayPresentationCommand command)
    {
        OverlayConfiguration existing = state.OverlayConfiguration;
        var updatedConfiguration = new OverlayConfiguration(
            existing.SchemaVersion,
            existing.Endpoint,
            new TotalDeathsOverlayOptions(command.IsTotalDeathsEnabled, command.ShowGameName, existing.TotalDeaths.CompactTitle, existing.TotalDeaths.Appearance, existing.TotalDeaths.TitleIconMode),
            new BossListOverlayOptions(command.IsBossListEnabled, command.BossListVisibilityMode, existing.BossList.Appearance, existing.BossList.DefeatedColor, existing.BossList.DefeatedTreatment, existing.BossList.ShowCheckmark, existing.BossList.CheckmarkAccent, existing.BossList.MaximumVisibleCount, existing.BossList.ShowDefeatedSkull, existing.BossList.CenterMarkerAlignment));

        if (PresentationEquals(existing, updatedConfiguration))
        {
            return Unchanged(state, TrackerCommandType.UpdateOverlayPresentation);
        }

        return Changed(
            new PersistentTrackerState(
                state.SchemaVersion,
                state.SelectedGameId,
                state.ManualBloodborneDeathCounter,
                state.BossProgress,
                updatedConfiguration, state.ManualBloodborneHotkeys, state.DeathSound, state.TextExports, state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave),
            TrackerCommandType.UpdateOverlayPresentation);
    }

    private static TrackerTransitionResult ResetOverlayAppearance(PersistentTrackerState state, ResetOverlayAppearanceCommand command)
    {
        OverlayConfiguration existing = state.OverlayConfiguration;
        OverlayConfiguration updated = command.IsTotalDeathsOverlay
            ? new OverlayConfiguration(existing.SchemaVersion, existing.Endpoint,
                new TotalDeathsOverlayOptions(existing.TotalDeaths.IsEnabled, existing.TotalDeaths.ShowGameName, existing.TotalDeaths.CompactTitle, OverlayAppearance.Default, existing.TotalDeaths.TitleIconMode), existing.BossList)
            : new OverlayConfiguration(existing.SchemaVersion, existing.Endpoint, existing.TotalDeaths,
                new BossListOverlayOptions(existing.BossList.IsEnabled, existing.BossList.VisibilityMode, OverlayAppearance.BossListDefault, existing.BossList.DefeatedColor, existing.BossList.DefeatedTreatment, existing.BossList.ShowCheckmark, existing.BossList.CheckmarkAccent, existing.BossList.MaximumVisibleCount, existing.BossList.ShowDefeatedSkull, existing.BossList.CenterMarkerAlignment));
        return Changed(new PersistentTrackerState(state.SchemaVersion, state.SelectedGameId, state.ManualBloodborneDeathCounter, state.BossProgress, updated, state.ManualBloodborneHotkeys, state.DeathSound, state.TextExports, state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave), TrackerCommandType.ResetOverlayAppearance);
    }

    private static TrackerTransitionResult ApplyUpdateOverlayAppearance(PersistentTrackerState state, UpdateOverlayAppearanceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command.Appearance);
        OverlayConfiguration existing = state.OverlayConfiguration;
        OverlayConfiguration updated = command.IsTotalDeathsOverlay
            ? new OverlayConfiguration(existing.SchemaVersion, existing.Endpoint,
                new TotalDeathsOverlayOptions(existing.TotalDeaths.IsEnabled, command.TotalDeathsShowGameName, command.TotalDeathsCompactTitle, command.Appearance, command.TotalDeathsTitleIconMode), existing.BossList)
            : new OverlayConfiguration(existing.SchemaVersion, existing.Endpoint, existing.TotalDeaths,
                new BossListOverlayOptions(existing.BossList.IsEnabled, command.BossListVisibilityMode, command.Appearance, command.BossListDefeatedColor, command.BossListDefeatedTreatment, command.BossListShowCheckmark, command.BossListCheckmarkAccent, command.BossListMaximumVisibleCount, command.BossListShowDefeatedSkull, command.BossListCenterMarkerAlignment));
        return Changed(new PersistentTrackerState(state.SchemaVersion, state.SelectedGameId, state.ManualBloodborneDeathCounter, state.BossProgress, updated, state.ManualBloodborneHotkeys, state.DeathSound, state.TextExports, state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave), TrackerCommandType.UpdateOverlayAppearance);
    }

    private static TrackerTransitionResult ApplyUpdateDeathSoundConfiguration(PersistentTrackerState state, UpdateDeathSoundConfigurationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command.Configuration);
        if (state.DeathSound == command.Configuration)
        {
            return Unchanged(state, TrackerCommandType.UpdateDeathSoundConfiguration);
        }

        return Changed(new PersistentTrackerState(state.SchemaVersion, state.SelectedGameId, state.ManualBloodborneDeathCounter, state.BossProgress, state.OverlayConfiguration, state.ManualBloodborneHotkeys, command.Configuration, state.TextExports, state.ManualDemonsSoulsDeathCounter, state.EldenRingNoticeAcknowledged, state.EldenRingSave), TrackerCommandType.UpdateDeathSoundConfiguration);
    }

    private static TrackerTransitionResult ApplyAcknowledgeEldenRingNotice(PersistentTrackerState state) =>
        state.EldenRingNoticeAcknowledged
            ? Unchanged(state, TrackerCommandType.AcknowledgeEldenRingNotice)
            : Changed(
                new PersistentTrackerState(
                    state.SchemaVersion,
                    state.SelectedGameId,
                    state.ManualBloodborneDeathCounter,
                    state.BossProgress,
                    state.OverlayConfiguration,
                    state.ManualBloodborneHotkeys,
                    state.DeathSound,
                    state.TextExports,
                    state.ManualDemonsSoulsDeathCounter,
                    eldenRingNoticeAcknowledged: true,
                    state.EldenRingSave),
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
                state.BossProgress,
                state.OverlayConfiguration,
                state.ManualBloodborneHotkeys,
                state.DeathSound,
                state.TextExports,
                state.ManualDemonsSoulsDeathCounter,
                state.EldenRingNoticeAcknowledged,
                command.Configuration),
            TrackerCommandType.UpdateEldenRingSaveConfiguration);
    }

    private static bool PresentationEquals(OverlayConfiguration left, OverlayConfiguration right) =>
        left.TotalDeaths.IsEnabled == right.TotalDeaths.IsEnabled &&
        left.TotalDeaths.ShowGameName == right.TotalDeaths.ShowGameName &&
        left.BossList.IsEnabled == right.BossList.IsEnabled &&
        left.BossList.VisibilityMode == right.BossList.VisibilityMode;

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
