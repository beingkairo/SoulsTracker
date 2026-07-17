namespace SoulsTracker.Domain;

/// <summary>
/// Holds the immutable, persisted tracker state shared by later application and
/// persistence work. Runtime reader observations do not belong in this state.
/// </summary>
public sealed class PersistentTrackerState
{
    /// <summary>
    /// Gets the only schema version supported by this contract.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Gets the deterministic empty persisted state.
    /// </summary>
    public static PersistentTrackerState Default { get; } = new(
        CurrentSchemaVersion,
        selectedGameId: null,
        ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
        BossProgress.Empty,
        OverlayConfiguration.Default,
        ManualBloodborneHotkeyConfiguration.Default,
        DeathSoundConfiguration.Default,
        TextExportConfiguration.Default,
        ManualBloodborneDeathCounter.CreateFor(GameId.DemonsSouls));

    /// <summary>
    /// Initializes validated persisted tracker state.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the schema version is unsupported.</exception>
    /// <exception cref="ArgumentException">Thrown when a selected game is unknown or disabled.</exception>
    public PersistentTrackerState(
        int schemaVersion,
        GameId? selectedGameId,
        ManualBloodborneDeathCounter manualBloodborneDeathCounter,
        BossProgress bossProgress,
        OverlayConfiguration overlayConfiguration,
        ManualBloodborneHotkeyConfiguration? manualBloodborneHotkeys = null,
        DeathSoundConfiguration? deathSound = null,
        TextExportConfiguration? textExports = null,
        ManualBloodborneDeathCounter? manualDemonsSoulsDeathCounter = null)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "The persistent tracker state schema version is unsupported.");
        }

        ValidateSelectedGame(selectedGameId);
        ArgumentNullException.ThrowIfNull(manualBloodborneDeathCounter);
        ArgumentNullException.ThrowIfNull(bossProgress);
        ArgumentNullException.ThrowIfNull(overlayConfiguration);

        SchemaVersion = schemaVersion;
        SelectedGameId = selectedGameId;
        ManualBloodborneDeathCounter = manualBloodborneDeathCounter;
        ManualDemonsSoulsDeathCounter = manualDemonsSoulsDeathCounter ?? ManualBloodborneDeathCounter.CreateFor(GameId.DemonsSouls);
        BossProgress = bossProgress;
        OverlayConfiguration = overlayConfiguration;
        ManualBloodborneHotkeys = manualBloodborneHotkeys is { IsValid: true } validHotkeys
            ? validHotkeys
            : ManualBloodborneHotkeyConfiguration.Default;
        DeathSound = deathSound ?? DeathSoundConfiguration.Default;
        TextExports = textExports ?? TextExportConfiguration.Default;
    }

    /// <summary>
    /// Gets the persisted schema version.
    /// </summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// Gets the selected canonical game, or <see langword="null"/> before a
    /// later application command selects one.
    /// </summary>
    public GameId? SelectedGameId { get; }

    /// <summary>
    /// Gets the persisted Bloodborne manual death counter.
    /// </summary>
    public ManualBloodborneDeathCounter ManualBloodborneDeathCounter { get; }

    /// <summary>Gets the persisted Demon’s Souls manual death counter.</summary>
    public ManualBloodborneDeathCounter ManualDemonsSoulsDeathCounter { get; }

    /// <summary>Returns the independent manual counter for a supported manual profile.</summary>
    public ManualBloodborneDeathCounter GetManualDeathCounter(GameId gameId) => gameId == GameId.Bloodborne
        ? ManualBloodborneDeathCounter
        : gameId == GameId.DemonsSouls
            ? ManualDemonsSoulsDeathCounter
            : throw new InvalidOperationException("The selected game does not use a manual death counter.");

    /// <summary>
    /// Gets immutable, game-scoped boss progress.
    /// </summary>
    public BossProgress BossProgress { get; }

    /// <summary>
    /// Gets the validated overlay configuration.
    /// </summary>
    public OverlayConfiguration OverlayConfiguration { get; }

    public ManualBloodborneHotkeyConfiguration ManualBloodborneHotkeys { get; }

    public DeathSoundConfiguration DeathSound { get; }
    public TextExportConfiguration TextExports { get; }

    private static void ValidateSelectedGame(GameId? selectedGameId)
    {
        if (selectedGameId is null)
        {
            return;
        }

        GameDefinition definition = GameCatalog.GetRequired(selectedGameId);
        if (!definition.IsSelectable)
        {
            throw new ArgumentException("A disabled SOON game cannot be selected.", nameof(selectedGameId));
        }
    }
}
