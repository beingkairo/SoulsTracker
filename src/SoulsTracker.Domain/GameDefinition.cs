using System.Collections.ObjectModel;

namespace SoulsTracker.Domain;

/// <summary>
/// Describes whether a game is currently selectable in the desktop workflow.
/// </summary>
public enum GameUiAvailability
{
    Selectable,
    DisabledSoon,
}

/// <summary>
/// Describes the approved death-tracking behavior for a game.
/// </summary>
public enum GameTrackingMode
{
    GameLifetimeReadOnly,
    ManualOnly,
    Unavailable,
}

/// <summary>
/// Describes whether reader work is permitted in the catalog at this phase.
/// </summary>
public enum ReaderBindingState
{
    PendingVerification,
    IntentionallyUnavailable,
}

/// <summary>
/// Provides immutable metadata and ordered boss definitions for one game.
/// P2-01 intentionally contains no process identity or reader factory.
/// </summary>
public sealed class GameDefinition
{
    private readonly ReadOnlyDictionary<BossId, BossDefinition> bossesById;

    /// <summary>
    /// Initializes a game definition with an optional ordered boss catalog.
    /// </summary>
    public GameDefinition(
        GameId id,
        string displayName,
        GameUiAvailability uiAvailability,
        GameTrackingMode trackingMode,
        ReaderBindingState readerBindingState,
        IEnumerable<BossDefinition>? bossCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("The game display name cannot be blank.", nameof(displayName));
        }

        BossDefinition[] catalog = bossCatalog?.ToArray() ?? [];
        if (catalog.Any(static boss => boss is null))
        {
            throw new ArgumentException("The boss catalog cannot contain null entries.", nameof(bossCatalog));
        }

        if (catalog.Select(static boss => boss.Id).Distinct().Count() != catalog.Length)
        {
            throw new ArgumentException("Boss IDs must be unique within a game catalog.", nameof(bossCatalog));
        }

        ValidateCapabilities(id, uiAvailability, trackingMode, readerBindingState, catalog);

        Id = id;
        DisplayName = displayName;
        UiAvailability = uiAvailability;
        TrackingMode = trackingMode;
        ReaderBindingState = readerBindingState;
        BossCatalog = Array.AsReadOnly(catalog);
        bossesById = new ReadOnlyDictionary<BossId, BossDefinition>(
            catalog.ToDictionary(static boss => boss.Id, static boss => boss));
    }

    /// <summary>
    /// Gets the canonical game ID.
    /// </summary>
    public GameId Id { get; }

    /// <summary>
    /// Gets the user-facing game name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets whether a later application command may select this game.
    /// </summary>
    public GameUiAvailability UiAvailability { get; }

    /// <summary>
    /// Gets the approved tracking mode.
    /// </summary>
    public GameTrackingMode TrackingMode { get; }

    /// <summary>
    /// Gets the P2-01 reader-binding state. No P2-01 definition has a usable binding.
    /// </summary>
    public ReaderBindingState ReaderBindingState { get; }

    /// <summary>
    /// Gets the immutable ordered catalog, if V1 includes one for the game.
    /// </summary>
    public IReadOnlyList<BossDefinition> BossCatalog { get; }

    /// <summary>
    /// Gets whether the game is selectable in a later application workflow.
    /// </summary>
    public bool IsSelectable => UiAvailability == GameUiAvailability.Selectable;

    internal BossDefinition GetRequiredBoss(BossId bossId)
    {
        ArgumentNullException.ThrowIfNull(bossId);

        if (bossesById.TryGetValue(bossId, out BossDefinition? boss))
        {
            return boss;
        }

        throw new ArgumentException(
            $"Boss ID '{bossId}' is not part of the '{Id}' catalog.",
            nameof(bossId));
    }

    private static void ValidateCapabilities(
        GameId id,
        GameUiAvailability uiAvailability,
        GameTrackingMode trackingMode,
        ReaderBindingState readerBindingState,
        BossDefinition[] bossCatalog)
    {
        if (uiAvailability == GameUiAvailability.DisabledSoon)
        {
            if (trackingMode != GameTrackingMode.Unavailable ||
                readerBindingState != ReaderBindingState.IntentionallyUnavailable ||
                bossCatalog.Length != 0)
            {
                throw new ArgumentException(
                    "A disabled SOON game must be unavailable, intentionally unbound, and have no V1 boss catalog.");
            }

            return;
        }

        if (id == GameId.Bloodborne || id == GameId.DemonsSouls)
        {
            if (trackingMode != GameTrackingMode.ManualOnly ||
                readerBindingState != ReaderBindingState.IntentionallyUnavailable)
            {
                throw new ArgumentException(
                    "Manual PlayStation profiles must remain manual-only.");
            }

            return;
        }

        // Elden Ring is selectable only after the user acknowledges its local
        // notice. Its reader remains intentionally unavailable until separate
        // live validation authorizes one.
        if (id == GameId.EldenRing &&
            trackingMode == GameTrackingMode.Unavailable &&
            readerBindingState == ReaderBindingState.IntentionallyUnavailable &&
            bossCatalog.Length == 0)
        {
            return;
        }

        if (trackingMode != GameTrackingMode.GameLifetimeReadOnly ||
            readerBindingState != ReaderBindingState.PendingVerification)
        {
            throw new ArgumentException(
                "Selectable non-Bloodborne games must be pending game-lifetime read-only verification.");
        }
    }
}
