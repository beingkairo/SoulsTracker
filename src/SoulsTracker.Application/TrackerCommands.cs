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
/// Updates the persisted presentation choices for the two read-only browser overlays.
/// </summary>
public sealed record UpdateOverlayPresentationCommand(
    bool IsTotalDeathsEnabled,
    bool ShowGameName) : ITrackerCommand;

/// <summary>Applies one approved, bounded appearance preset to one browser overlay.</summary>
public sealed record ResetOverlayAppearanceCommand(bool IsTotalDeathsOverlay) : ITrackerCommand;

/// <summary>Replaces one overlay's validated, typed presentation settings.</summary>
public sealed record UpdateOverlayAppearanceCommand(
    bool IsTotalDeathsOverlay,
    OverlayAppearance Appearance,
    bool TotalDeathsShowGameName,
    bool TotalDeathsCompactTitle,
    OverlayTitleIconMode TotalDeathsTitleIconMode = OverlayTitleIconMode.Off) : ITrackerCommand;


/// <summary>Stores the local acknowledgement required before selecting Elden Ring.</summary>
public sealed record AcknowledgeEldenRingNoticeCommand : ITrackerCommand;

/// <summary>Updates the locally selected, read-only Elden Ring save and profile slot.</summary>
public sealed record UpdateEldenRingSaveConfigurationCommand(EldenRingSaveConfiguration Configuration) : ITrackerCommand;

/// <summary>Adds or removes one confirmed Elden Ring death the save omits.</summary>
public sealed record AdjustEldenRingMissedDeathsCommand(bool Increment) : ITrackerCommand;

/// <summary>Updates the locally selected, read-only Black Myth: Wukong save.</summary>
public sealed record UpdateBlackMythWukongSaveConfigurationCommand(BlackMythWukongSaveConfiguration Configuration) : ITrackerCommand;

/// <summary>Updates the locally selected, read-only Lies of P Steam save.</summary>
public sealed record UpdateLiesOfPSaveConfigurationCommand(LiesOfPSaveConfiguration Configuration) : ITrackerCommand;

/// <summary>
/// Identifies the command whose transition was evaluated without carrying state or secrets.
/// </summary>
public enum TrackerCommandType
{
    SelectGame,
    IncrementManualBloodborneDeaths,
    DecrementManualBloodborneDeaths,

    UpdateOverlayPresentation,
    ResetOverlayAppearance,
    UpdateOverlayAppearance,

    AcknowledgeEldenRingNotice,
    UpdateEldenRingSaveConfiguration,
    AdjustEldenRingMissedDeaths,
    UpdateBlackMythWukongSaveConfiguration,
    UpdateLiesOfPSaveConfiguration,
    UpdateTextExports,
    LegacyImport,
}
