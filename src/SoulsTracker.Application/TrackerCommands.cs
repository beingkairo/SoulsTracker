using SoulsTracker.Domain;

namespace SoulsTracker.Application;

/// <summary>
/// Marks a value as an approved, immutable tracker-state command.
/// </summary>
public interface ITrackerCommand;

/// <summary>
/// Selects one canonical, selectable game.
/// </summary>
public sealed record SelectGameCommand(GameId GameId) : ITrackerCommand;

/// <summary>
/// Adds exactly one streamer-controlled Bloodborne death.
/// </summary>
public sealed record IncrementManualBloodborneDeathsCommand : ITrackerCommand;

/// <summary>
/// Removes exactly one streamer-controlled Bloodborne death when above zero.
/// </summary>
public sealed record DecrementManualBloodborneDeathsCommand : ITrackerCommand;

/// <summary>
/// Sets the defeated state of one canonical boss in one canonical game's catalog.
/// </summary>
public sealed record SetBossDefeatedCommand(GameId GameId, BossId BossId, bool IsDefeated) : ITrackerCommand;

/// <summary>
/// Updates the persisted presentation choices for the two read-only browser overlays.
/// </summary>
public sealed record UpdateOverlayPresentationCommand(
    bool IsTotalDeathsEnabled,
    bool ShowGameName,
    bool IsBossListEnabled,
    BossListVisibilityMode BossListVisibilityMode) : ITrackerCommand;

/// <summary>Applies one approved, bounded appearance preset to one browser overlay.</summary>
public sealed record ResetOverlayAppearanceCommand(bool IsTotalDeathsOverlay) : ITrackerCommand;

/// <summary>Replaces one overlay's validated, typed presentation settings.</summary>
public sealed record UpdateOverlayAppearanceCommand(
    bool IsTotalDeathsOverlay,
    OverlayAppearance Appearance,
    bool TotalDeathsShowGameName,
    bool TotalDeathsCompactTitle,
    BossListVisibilityMode BossListVisibilityMode,
    string BossListDefeatedColor,
    DefeatedBossTreatment BossListDefeatedTreatment,
    bool BossListShowCheckmark,
    string BossListCheckmarkAccent,
    int BossListMaximumVisibleCount,
    OverlayTitleIconMode TotalDeathsTitleIconMode = OverlayTitleIconMode.Off,
    bool BossListShowDefeatedSkull = false,
    CenterMarkerAlignment BossListCenterMarkerAlignment = CenterMarkerAlignment.Left) : ITrackerCommand;

/// <summary>Replaces the validated local-only death sound configuration.</summary>
public sealed record UpdateDeathSoundConfigurationCommand(DeathSoundConfiguration Configuration) : ITrackerCommand;

/// <summary>Stores the local acknowledgement required before selecting Elden Ring.</summary>
public sealed record AcknowledgeEldenRingNoticeCommand : ITrackerCommand;

/// <summary>
/// Identifies the command whose transition was evaluated without carrying state or secrets.
/// </summary>
public enum TrackerCommandType
{
    SelectGame,
    IncrementManualBloodborneDeaths,
    DecrementManualBloodborneDeaths,
    SetBossDefeated,
    UpdateOverlayPresentation,
    ResetOverlayAppearance,
    UpdateOverlayAppearance,
    UpdateDeathSoundConfiguration,
    AcknowledgeEldenRingNotice,
    UpdateTextExports,
    LegacyImport,
}
