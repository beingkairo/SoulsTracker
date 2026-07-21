using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace SoulsTracker.Domain;

/// <summary>
/// Identifies one supported or visible game using its canonical persisted ID.
/// </summary>
public sealed record GameId
{
    private GameId(string value)
    {
        Value = value;
    }

    public static GameId Ds1 { get; } = new("ds1");

    public static GameId Ds2 { get; } = new("ds2");

    public static GameId Ds3 { get; } = new("ds3");

    public static GameId Sekiro { get; } = new("sekiro");

    public static GameId Bloodborne { get; } = new("bloodborne");

    public static GameId EldenRing { get; } = new("elden_ring");

    public static GameId DemonsSouls { get; } = new("demons_souls");

    private static readonly ReadOnlyCollection<GameId> AllGameIds = Array.AsReadOnly(
    [
        DemonsSouls,
        Ds1,
        Ds2,
        Ds3,
        Bloodborne,
        Sekiro,
        EldenRing,
    ]);

    private static readonly ReadOnlyDictionary<string, GameId> KnownGameIds =
        new ReadOnlyDictionary<string, GameId>(new Dictionary<string, GameId>(StringComparer.Ordinal)
        {
            [Ds1.Value] = Ds1,
            [Ds2.Value] = Ds2,
            [Ds3.Value] = Ds3,
            [Sekiro.Value] = Sekiro,
            [Bloodborne.Value] = Bloodborne,
            [EldenRing.Value] = EldenRing,
            [DemonsSouls.Value] = DemonsSouls,
        });

    /// <summary>
    /// Gets every canonical game ID in the catalog order.
    /// </summary>
    public static IReadOnlyList<GameId> All => AllGameIds;

    /// <summary>
    /// Gets the stable identifier used in state and catalog lookups.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Parses an exact canonical game ID without applying legacy normalization
    /// or a fallback game.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is not canonical.</exception>
    public static GameId Parse(string value)
    {
        if (TryParse(value, out GameId? gameId))
        {
            return gameId;
        }

        throw new ArgumentException("The game ID must be a known canonical ID.", nameof(value));
    }

    /// <summary>
    /// Attempts to parse an exact canonical game ID without normalization.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out GameId? gameId)
    {
        if (value is not null && KnownGameIds.TryGetValue(value, out GameId? knownGameId))
        {
            gameId = knownGameId;
            return true;
        }

        gameId = null;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
