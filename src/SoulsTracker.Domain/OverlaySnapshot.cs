using System.Collections.ObjectModel;

namespace SoulsTracker.Domain;

/// <summary>
/// Identifies the source and availability of a Total Deaths display value.
/// </summary>
public enum TotalDeathsDisplaySource
{
    Unavailable,
    ManualBloodborne,
    GameLifetimeReader,
}

/// <summary>
/// Provides canonical selected-game metadata for an overlay snapshot.
/// </summary>
public sealed class OverlayGameMetadata
{
    /// <summary>
    /// Initializes metadata for one selectable game.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the game is unknown or disabled.</exception>
    public OverlayGameMetadata(GameId gameId)
    {
        ArgumentNullException.ThrowIfNull(gameId);

        GameDefinition definition = GameCatalog.GetRequired(gameId);
        if (!definition.IsSelectable)
        {
            throw new ArgumentException("A disabled SOON game cannot have overlay metadata.", nameof(gameId));
        }

        GameId = gameId;
        DisplayName = definition.DisplayName;
    }

    /// <summary>
    /// Gets the canonical selected game ID.
    /// </summary>
    public GameId GameId { get; }

    /// <summary>
    /// Gets the canonical display name for the selected game.
    /// </summary>
    public string DisplayName { get; }
}

/// <summary>
/// Represents a validated Total Deaths value suitable for read-only display.
/// </summary>
public sealed class TotalDeathsDisplayValue
{
    private TotalDeathsDisplayValue(TotalDeathsDisplaySource source, GameId? gameId, long? value)
    {
        Source = source;
        GameId = gameId;
        Value = value;
    }

    /// <summary>
    /// Gets the unavailable display value, which contains no numeric value or game ID.
    /// </summary>
    public static TotalDeathsDisplayValue Unavailable { get; } = new(
        TotalDeathsDisplaySource.Unavailable,
        gameId: null,
        value: null);

    /// <summary>
    /// Creates a manual display value from the sole approved manual counter.
    /// </summary>
    public static TotalDeathsDisplayValue FromManualBloodborneCounter(
        ManualBloodborneDeathCounter manualBloodborneDeathCounter)
        => FromManualCounter(GameId.Bloodborne, manualBloodborneDeathCounter);

    /// <summary>Creates a manual display value for either supported manual profile.</summary>
    public static TotalDeathsDisplayValue FromManualCounter(
        GameId gameId,
        ManualBloodborneDeathCounter manualDeathCounter)
    {
        ArgumentNullException.ThrowIfNull(gameId);
        ArgumentNullException.ThrowIfNull(manualDeathCounter);
        if (gameId != GameId.Bloodborne && gameId != GameId.DemonsSouls)
        {
            throw new ArgumentException("Manual Total Deaths is available only for the supported manual profiles.", nameof(gameId));
        }

        return new TotalDeathsDisplayValue(
            TotalDeathsDisplaySource.ManualBloodborne,
            gameId,
            manualDeathCounter.Value);
    }

    /// <summary>
    /// Creates a reader display value from a validated runtime observation.
    /// </summary>
    public static TotalDeathsDisplayValue FromRuntimeObservation(
        RuntimeGameObservation runtimeObservation)
    {
        ArgumentNullException.ThrowIfNull(runtimeObservation);

        return new TotalDeathsDisplayValue(
            TotalDeathsDisplaySource.GameLifetimeReader,
            runtimeObservation.GameId,
            runtimeObservation.TotalDeaths.Value);
    }

    /// <summary>
    /// Gets the explicit availability and source status.
    /// </summary>
    public TotalDeathsDisplaySource Source { get; }

    /// <summary>
    /// Gets the game that supplied the value, or <see langword="null"/> when unavailable.
    /// </summary>
    public GameId? GameId { get; }

    /// <summary>
    /// Gets the non-negative numeric display value, or <see langword="null"/> when unavailable.
    /// </summary>
    public long? Value { get; }
}

/// <summary>
/// Provides one typed boss display entry and its defeated state.
/// </summary>
public sealed class OverlayBossEntry
{
    /// <summary>
    /// Initializes a display entry from canonical boss metadata.
    /// </summary>
    public OverlayBossEntry(BossDefinition bossDefinition, bool isDefeated)
    {
        ArgumentNullException.ThrowIfNull(bossDefinition);

        BossId = bossDefinition.Id;
        DisplayName = bossDefinition.DisplayName;
        DlcLabel = bossDefinition.DlcLabel;
        IsDefeated = isDefeated;
    }

    /// <summary>
    /// Gets the stable boss ID.
    /// </summary>
    public BossId BossId { get; }

    /// <summary>
    /// Gets the canonical boss display name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the optional canonical DLC grouping label.
    /// </summary>
    public string? DlcLabel { get; }

    /// <summary>
    /// Gets whether the boss is defeated for the selected game.
    /// </summary>
    public bool IsDefeated { get; }
}

/// <summary>
/// Contains the secret-free, validated presentation choices an overlay browser
/// needs. Endpoint credentials deliberately do not cross this boundary.
/// </summary>
public sealed class OverlayPresentationConfiguration
{
    /// <summary>
    /// Creates the browser-safe projection of persisted overlay configuration.
    /// </summary>
    public static OverlayPresentationConfiguration From(OverlayConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new OverlayPresentationConfiguration(
            configuration.TotalDeaths.IsEnabled,
            configuration.TotalDeaths.ShowGameName,
            configuration.BossList.IsEnabled,
            configuration.BossList.VisibilityMode,
            configuration.TotalDeaths.CompactTitle,
            configuration.TotalDeaths.TitleIconMode,
            configuration.TotalDeaths.Appearance,
            configuration.BossList.Appearance,
            configuration.BossList.DefeatedColor,
            configuration.BossList.DefeatedTreatment,
            configuration.BossList.ShowCheckmark,
            configuration.BossList.CheckmarkAccent,
            configuration.BossList.MaximumVisibleCount,
            configuration.BossList.ShowDefeatedSkull,
            configuration.BossList.CenterMarkerAlignment);
    }

    /// <summary>
    /// Initializes a validated browser-safe overlay presentation projection.
    /// </summary>
    public OverlayPresentationConfiguration(
        bool isTotalDeathsEnabled,
        bool showGameName,
        bool isBossListEnabled,
        BossListVisibilityMode bossListVisibilityMode,
        bool totalDeathsCompactTitle = false,
        OverlayTitleIconMode totalDeathsTitleIconMode = OverlayTitleIconMode.Off,
        OverlayAppearance? totalDeathsAppearance = null,
        OverlayAppearance? bossListAppearance = null,
        string bossListDefeatedColor = "#8C8C96",
        DefeatedBossTreatment bossListDefeatedTreatment = DefeatedBossTreatment.Nothing,
        bool bossListShowCheckmark = true,
        string bossListCheckmarkAccent = "#A78BFA",
        int bossListMaximumVisibleCount = 25,
        bool bossListShowDefeatedSkull = false,
        CenterMarkerAlignment bossListCenterMarkerAlignment = CenterMarkerAlignment.Left)
    {
        if (!Enum.IsDefined(bossListVisibilityMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bossListVisibilityMode),
                bossListVisibilityMode,
                "The boss-list visibility mode is not supported.");
        }

        IsTotalDeathsEnabled = isTotalDeathsEnabled;
        ShowGameName = false;
        IsBossListEnabled = isBossListEnabled;
        BossListVisibilityMode = bossListVisibilityMode;
        TotalDeathsCompactTitle = true;
        TotalDeathsTitleIconMode = Enum.IsDefined(totalDeathsTitleIconMode) ? totalDeathsTitleIconMode : OverlayTitleIconMode.Off;
        TotalDeathsAppearance = (totalDeathsAppearance ?? OverlayAppearance.Default).WithAlignment(OverlayTextAlignment.Left);
        BossListAppearance = bossListAppearance ?? OverlayAppearance.BossListDefault;
        BossListDefeatedColor = bossListDefeatedColor;
        BossListDefeatedTreatment = bossListDefeatedTreatment;
        BossListShowCheckmark = bossListShowCheckmark;
        BossListCheckmarkAccent = bossListCheckmarkAccent;
        BossListMaximumVisibleCount = bossListMaximumVisibleCount;
        BossListShowDefeatedSkull = bossListShowDefeatedSkull;
        BossListCenterMarkerAlignment = Enum.IsDefined(bossListCenterMarkerAlignment) ? bossListCenterMarkerAlignment : CenterMarkerAlignment.Left;
    }

    /// <summary>Gets whether the Total Deaths layout is visible.</summary>
    public bool IsTotalDeathsEnabled { get; }

    /// <summary>Gets whether the selected game name is visible with Total Deaths.</summary>
    public bool ShowGameName { get; }

    /// <summary>Gets whether the boss-list layout is visible.</summary>
    public bool IsBossListEnabled { get; }

    /// <summary>Gets the validated boss-list visibility filter.</summary>
    public BossListVisibilityMode BossListVisibilityMode { get; }
    public bool TotalDeathsCompactTitle { get; }
    public OverlayTitleIconMode TotalDeathsTitleIconMode { get; }
    public OverlayAppearance TotalDeathsAppearance { get; }
    public OverlayAppearance BossListAppearance { get; }
    public string BossListDefeatedColor { get; }
    public DefeatedBossTreatment BossListDefeatedTreatment { get; }
    public bool BossListShowCheckmark { get; }
    public string BossListCheckmarkAccent { get; }
    public int BossListMaximumVisibleCount { get; }
    public bool BossListShowDefeatedSkull { get; }
    public CenterMarkerAlignment BossListCenterMarkerAlignment { get; }
}

/// <summary>
/// Holds an immutable, typed read-only overlay payload.
/// </summary>
public sealed class OverlaySnapshot
{
    /// <summary>
    /// Gets the only schema version supported by this contract.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Initializes a validated, read-only overlay snapshot.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the selected game, death display, timestamp, or boss entries conflict.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the schema version or sequence number is unsupported.</exception>
    public OverlaySnapshot(
        int schemaVersion,
        long sequenceNumber,
        DateTimeOffset generatedAtUtc,
        OverlayGameMetadata? selectedGame,
        TotalDeathsDisplayValue totalDeaths,
        IEnumerable<OverlayBossEntry> bosses)
        : this(
            schemaVersion,
            sequenceNumber,
            generatedAtUtc,
            selectedGame,
            totalDeaths,
            bosses,
            OverlayPresentationConfiguration.From(OverlayConfiguration.Default))
    {
    }

    /// <summary>
    /// Initializes a validated, read-only snapshot with its secret-free
    /// presentation projection.
    /// </summary>
    public OverlaySnapshot(
        int schemaVersion,
        long sequenceNumber,
        DateTimeOffset generatedAtUtc,
        OverlayGameMetadata? selectedGame,
        TotalDeathsDisplayValue totalDeaths,
        IEnumerable<OverlayBossEntry> bosses,
        OverlayPresentationConfiguration presentation)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "The overlay snapshot schema version is unsupported.");
        }

        if (sequenceNumber < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceNumber),
                sequenceNumber,
                "The overlay snapshot sequence number cannot be negative.");
        }

        if (generatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The overlay snapshot timestamp must be UTC.", nameof(generatedAtUtc));
        }

        ArgumentNullException.ThrowIfNull(totalDeaths);
        ArgumentNullException.ThrowIfNull(bosses);
        ArgumentNullException.ThrowIfNull(presentation);

        OverlayBossEntry[] bossEntries = bosses.ToArray();
        if (bossEntries.Any(static entry => entry is null))
        {
            throw new ArgumentException("The overlay boss list cannot contain null entries.", nameof(bosses));
        }

        ValidateSelectionAndDisplay(selectedGame, totalDeaths, bossEntries);

        SchemaVersion = schemaVersion;
        SequenceNumber = sequenceNumber;
        GeneratedAtUtc = generatedAtUtc;
        SelectedGame = selectedGame;
        TotalDeaths = totalDeaths;
        Bosses = Array.AsReadOnly(bossEntries);
        Presentation = presentation;
    }

    /// <summary>
    /// Gets the snapshot schema version.
    /// </summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// Gets the non-negative monotonically increasing snapshot sequence number.
    /// </summary>
    public long SequenceNumber { get; }

    /// <summary>
    /// Gets the UTC time at which this snapshot was generated.
    /// </summary>
    public DateTimeOffset GeneratedAtUtc { get; }

    /// <summary>
    /// Gets selected-game metadata, or <see langword="null"/> before selection.
    /// </summary>
    public OverlayGameMetadata? SelectedGame { get; }

    /// <summary>
    /// Gets the typed Total Deaths display value.
    /// </summary>
    public TotalDeathsDisplayValue TotalDeaths { get; }

    /// <summary>
    /// Gets the immutable ordered boss list.
    /// </summary>
    public IReadOnlyList<OverlayBossEntry> Bosses { get; }

    /// <summary>
    /// Gets the secret-free, validated presentation choices for this snapshot.
    /// </summary>
    public OverlayPresentationConfiguration Presentation { get; }

    private static void ValidateSelectionAndDisplay(
        OverlayGameMetadata? selectedGame,
        TotalDeathsDisplayValue totalDeaths,
        OverlayBossEntry[] bossEntries)
    {
        if (selectedGame is null)
        {
            if (totalDeaths.Source != TotalDeathsDisplaySource.Unavailable ||
                totalDeaths.GameId is not null ||
                totalDeaths.Value is not null)
            {
                throw new ArgumentException(
                    "An overlay snapshot without a selected game must have unavailable Total Deaths.",
                    nameof(totalDeaths));
            }

            if (bossEntries.Length != 0)
            {
                throw new ArgumentException(
                    "An overlay snapshot without a selected game cannot contain bosses.",
                    nameof(bossEntries));
            }

            return;
        }

        GameDefinition definition = GameCatalog.GetRequired(selectedGame.GameId);
        if (!definition.IsSelectable)
        {
            throw new ArgumentException("A disabled SOON game cannot be assigned an overlay state.", nameof(selectedGame));
        }

        ValidateTotalDeathsDisplay(definition, totalDeaths);
        ValidateBossEntries(definition, bossEntries);
    }

    private static void ValidateTotalDeathsDisplay(
        GameDefinition selectedDefinition,
        TotalDeathsDisplayValue totalDeaths)
    {
        switch (totalDeaths.Source)
        {
            case TotalDeathsDisplaySource.Unavailable:
                if (totalDeaths.GameId is not null || totalDeaths.Value is not null)
                {
                    throw new ArgumentException(
                        "Unavailable Total Deaths cannot include a game or numeric value.",
                        nameof(totalDeaths));
                }

                return;

            case TotalDeathsDisplaySource.ManualBloodborne:
                if ((selectedDefinition.Id != GameId.Bloodborne && selectedDefinition.Id != GameId.DemonsSouls) ||
                    totalDeaths.GameId != selectedDefinition.Id ||
                    totalDeaths.Value is null ||
                    totalDeaths.Value < 0)
                {
                    throw new ArgumentException(
                        "Manual Total Deaths must match the selected manual profile.",
                        nameof(totalDeaths));
                }

                return;

            case TotalDeathsDisplaySource.GameLifetimeReader:
                if (selectedDefinition.TrackingMode != GameTrackingMode.GameLifetimeReadOnly ||
                    totalDeaths.GameId != selectedDefinition.Id ||
                    totalDeaths.Value is null ||
                    totalDeaths.Value < 0)
                {
                    throw new ArgumentException(
                        "Reader Total Deaths must match the selected automatic game.",
                        nameof(totalDeaths));
                }

                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(totalDeaths),
                    totalDeaths.Source,
                    "The Total Deaths display source is not supported.");
        }
    }

    private static void ValidateBossEntries(GameDefinition selectedDefinition, OverlayBossEntry[] bossEntries)
    {
        HashSet<BossId> seenBossIds = [];

        foreach (OverlayBossEntry entry in bossEntries)
        {
            if (!seenBossIds.Add(entry.BossId))
            {
                throw new ArgumentException("The overlay boss list cannot contain duplicate boss IDs.", nameof(bossEntries));
            }

            BossDefinition canonicalBoss = selectedDefinition.GetRequiredBoss(entry.BossId);
            if (!string.Equals(canonicalBoss.DisplayName, entry.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(canonicalBoss.DlcLabel, entry.DlcLabel, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Overlay boss metadata must match the selected game's canonical catalog.",
                    nameof(bossEntries));
            }
        }
    }
}
