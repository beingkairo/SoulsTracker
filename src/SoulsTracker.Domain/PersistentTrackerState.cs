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
        selectedGameId: GameId.DemonsSouls,
        ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
        BossProgress.Empty,
        OverlayConfiguration.Default,
        ManualBloodborneHotkeyConfiguration.Default,
        DeathSoundConfiguration.Default,
        TextExportConfiguration.Default,
        ManualBloodborneDeathCounter.CreateFor(GameId.DemonsSouls),
        eldenRingNoticeAcknowledged: false,
        EldenRingSaveConfiguration.Default,
        BossListScope.AllBosses,
        BlackMythWukongSaveConfiguration.Default,
        EldenRingMissedDeathAdjustments.Empty,
        LiesOfPSaveConfiguration.Default);

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
        ManualBloodborneDeathCounter? manualDemonsSoulsDeathCounter = null,
        bool eldenRingNoticeAcknowledged = false,
        EldenRingSaveConfiguration? eldenRingSave = null,
        BossListScope bossListScope = BossListScope.AllBosses,
        BlackMythWukongSaveConfiguration? blackMythWukongSave = null,
        EldenRingMissedDeathAdjustments? eldenRingMissedDeathAdjustments = null,
        LiesOfPSaveConfiguration? liesOfPSave = null)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "The persistent tracker state schema version is unsupported.");
        }

        selectedGameId ??= GameId.DemonsSouls;
        ValidateSelectedGame(selectedGameId, eldenRingNoticeAcknowledged);
        ArgumentNullException.ThrowIfNull(manualBloodborneDeathCounter);
        ArgumentNullException.ThrowIfNull(bossProgress);
        ArgumentNullException.ThrowIfNull(overlayConfiguration);

        SchemaVersion = schemaVersion;
        SelectedGameId = selectedGameId;
        ManualBloodborneDeathCounter = manualBloodborneDeathCounter;
        ManualDemonsSoulsDeathCounter = manualDemonsSoulsDeathCounter ?? ManualBloodborneDeathCounter.CreateFor(GameId.DemonsSouls);
        BlackMythWukongSave = blackMythWukongSave ?? BlackMythWukongSaveConfiguration.Default;
        LiesOfPSave = liesOfPSave ?? LiesOfPSaveConfiguration.Default;
        BossProgress = bossProgress;
        OverlayConfiguration = overlayConfiguration;
        ManualBloodborneHotkeys = manualBloodborneHotkeys is { IsValid: true } validHotkeys
            ? validHotkeys
            : ManualBloodborneHotkeyConfiguration.Default;
        DeathSound = deathSound ?? DeathSoundConfiguration.Default;
        TextExports = textExports ?? TextExportConfiguration.Default;
        EldenRingNoticeAcknowledged = eldenRingNoticeAcknowledged;
        EldenRingSave = eldenRingSave ?? EldenRingSaveConfiguration.Default;
        EldenRingMissedDeathAdjustments = eldenRingMissedDeathAdjustments ?? EldenRingMissedDeathAdjustments.Empty;
        BossListScope = BossCatalogDisplayFilter.NormalizeScope(GameCatalog.GetRequired(selectedGameId), bossListScope);
    }

    /// <summary>
    /// Gets the persisted schema version.
    /// </summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// Gets the selected canonical game. Legacy absent values normalize to Demon Souls.
    /// </summary>
    public GameId SelectedGameId { get; }

    /// <summary>
    /// Gets the persisted Bloodborne manual death counter.
    /// </summary>
    public ManualBloodborneDeathCounter ManualBloodborneDeathCounter { get; }

    /// <summary>Gets the persisted Demon’s Souls manual death counter.</summary>
    public ManualBloodborneDeathCounter ManualDemonsSoulsDeathCounter { get; }

    /// <summary>Local configuration for the separate read-only Black Myth: Wukong save reader.</summary>
    public BlackMythWukongSaveConfiguration BlackMythWukongSave { get; }

    /// <summary>Local configuration for the separate read-only Lies of P Steam save reader.</summary>
    public LiesOfPSaveConfiguration LiesOfPSave { get; }

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

    /// <summary>Gets whether this local installation accepted the Elden Ring notice.</summary>
    public bool EldenRingNoticeAcknowledged { get; }

    /// <summary>Local configuration for the separate read-only Elden Ring save reader.</summary>
    public EldenRingSaveConfiguration EldenRingSave { get; }

    /// <summary>Gets local, per-save and per-character Elden Ring missed-death additions.</summary>
    public EldenRingMissedDeathAdjustments EldenRingMissedDeathAdjustments { get; }

    /// <summary>Gets the persisted scope shared by checklist, overlay, preview, and TXT export.</summary>
    public BossListScope BossListScope { get; }

    private static void ValidateSelectedGame(GameId? selectedGameId, bool eldenRingNoticeAcknowledged)
    {
        GameDefinition definition = GameCatalog.GetRequired(selectedGameId!);
        if (!definition.IsSelectable)
        {
            throw new ArgumentException("A disabled SOON game cannot be selected.", nameof(selectedGameId));
        }

        if (selectedGameId == GameId.EldenRing && !eldenRingNoticeAcknowledged)
        {
            throw new ArgumentException("Elden Ring requires local acknowledgement before selection.", nameof(selectedGameId));
        }
    }
}
