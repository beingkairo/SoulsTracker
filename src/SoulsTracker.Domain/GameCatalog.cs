using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace SoulsTracker.Domain;

/// <summary>Provides the canonical game definitions used by the death counter.</summary>
public static class GameCatalog
{
    private static readonly ReadOnlyCollection<GameDefinition> Definitions = Array.AsReadOnly<GameDefinition>(
    [
        new(GameId.DemonsSouls, "Demon Souls", GameUiAvailability.Selectable, GameTrackingMode.ManualOnly, ReaderBindingState.IntentionallyUnavailable),
        new(GameId.Ds1, "Dark Souls: Remastered", GameUiAvailability.Selectable, GameTrackingMode.GameLifetimeReadOnly, ReaderBindingState.PendingVerification),
        new(GameId.Ds2, "Dark Souls II: Scholar of the First Sin", GameUiAvailability.Selectable, GameTrackingMode.GameLifetimeReadOnly, ReaderBindingState.PendingVerification),
        new(GameId.Ds3, "Dark Souls III", GameUiAvailability.Selectable, GameTrackingMode.GameLifetimeReadOnly, ReaderBindingState.PendingVerification),
        new(GameId.Bloodborne, "Bloodborne", GameUiAvailability.Selectable, GameTrackingMode.GameLifetimeReadOnly, ReaderBindingState.PendingVerification),
        new(GameId.Sekiro, "Sekiro: Shadows Die Twice", GameUiAvailability.Selectable, GameTrackingMode.GameLifetimeReadOnly, ReaderBindingState.PendingVerification),
        new(GameId.EldenRing, "Elden Ring", GameUiAvailability.Selectable, GameTrackingMode.GameLifetimeReadOnly, ReaderBindingState.PendingVerification),
        new(GameId.BlackMythWukong, "Black Myth: Wukong", GameUiAvailability.Selectable, GameTrackingMode.GameLifetimeReadOnly, ReaderBindingState.PendingVerification),
        new(GameId.LiesOfP, "Lies of P", GameUiAvailability.Selectable, GameTrackingMode.GameLifetimeReadOnly, ReaderBindingState.PendingVerification),
    ]);

    private static readonly ReadOnlyDictionary<GameId, GameDefinition> DefinitionsById =
        new(Definitions.ToDictionary(static definition => definition.Id));

    public static IReadOnlyList<GameDefinition> All => Definitions;

    public static GameDefinition GetRequired(GameId gameId)
    {
        ArgumentNullException.ThrowIfNull(gameId);
        if (DefinitionsById.TryGetValue(gameId, out GameDefinition? definition)) return definition;
        throw new ArgumentException("The game ID is not in the canonical catalog.", nameof(gameId));
    }

    public static GameDefinition GetRequired(string gameId) => GetRequired(GameId.Parse(gameId));

    public static bool TryGet(string? gameId, [NotNullWhen(true)] out GameDefinition? definition)
    {
        if (GameId.TryParse(gameId, out GameId? parsed) && DefinitionsById.TryGetValue(parsed, out definition)) return true;
        definition = null;
        return false;
    }
}
