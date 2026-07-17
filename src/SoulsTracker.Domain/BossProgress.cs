using System.Collections.ObjectModel;

namespace SoulsTracker.Domain;

/// <summary>
/// Holds immutable defeated-boss state keyed by canonical game and boss IDs.
/// </summary>
public sealed class BossProgress
{
    private readonly IReadOnlyDictionary<GameId, IReadOnlySet<BossId>> defeatedBossesByGame;

    private BossProgress(IReadOnlyDictionary<GameId, IReadOnlySet<BossId>> defeatedBossesByGame)
    {
        this.defeatedBossesByGame = defeatedBossesByGame;
    }

    /// <summary>
    /// Gets an empty, immutable progress state.
    /// </summary>
    public static BossProgress Empty { get; } = new BossProgress(
        new ReadOnlyDictionary<GameId, IReadOnlySet<BossId>>(
            new Dictionary<GameId, IReadOnlySet<BossId>>()));

    /// <summary>
    /// Returns a new progress state with the supplied catalog boss marked defeated.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the game or boss is not in the matching catalog.</exception>
    public BossProgress MarkDefeated(GameId gameId, BossId bossId)
    {
        GameCatalog.GetRequiredBoss(gameId, bossId);

        if (defeatedBossesByGame.TryGetValue(gameId, out IReadOnlySet<BossId>? defeatedBosses) &&
            defeatedBosses.Contains(bossId))
        {
            return this;
        }

        Dictionary<GameId, IReadOnlySet<BossId>> copy = CopyDefeatedBosses();
        HashSet<BossId> updated = copy.TryGetValue(gameId, out IReadOnlySet<BossId>? existing)
            ? new HashSet<BossId>(existing)
            : [];
        updated.Add(bossId);
        copy[gameId] = updated;

        return new BossProgress(new ReadOnlyDictionary<GameId, IReadOnlySet<BossId>>(copy));
    }

    /// <summary>
    /// Returns a new progress state with the supplied catalog boss no longer
    /// marked defeated.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the game or boss is not in the matching catalog.</exception>
    public BossProgress ClearDefeated(GameId gameId, BossId bossId)
    {
        GameCatalog.GetRequiredBoss(gameId, bossId);

        if (!defeatedBossesByGame.TryGetValue(gameId, out IReadOnlySet<BossId>? defeatedBosses) ||
            !defeatedBosses.Contains(bossId))
        {
            return this;
        }

        Dictionary<GameId, IReadOnlySet<BossId>> copy = CopyDefeatedBosses();
        HashSet<BossId> updated = new(copy[gameId]);
        updated.Remove(bossId);

        if (updated.Count == 0)
        {
            copy.Remove(gameId);
        }
        else
        {
            copy[gameId] = updated;
        }

        return new BossProgress(new ReadOnlyDictionary<GameId, IReadOnlySet<BossId>>(copy));
    }

    /// <summary>
    /// Returns whether a known boss is defeated for its matching game only.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the game or boss is not in the matching catalog.</exception>
    public bool IsDefeated(GameId gameId, BossId bossId)
    {
        GameCatalog.GetRequiredBoss(gameId, bossId);
        return defeatedBossesByGame.TryGetValue(gameId, out IReadOnlySet<BossId>? defeatedBosses) &&
            defeatedBosses.Contains(bossId);
    }

    private Dictionary<GameId, IReadOnlySet<BossId>> CopyDefeatedBosses()
    {
        return defeatedBossesByGame.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlySet<BossId>)new HashSet<BossId>(pair.Value));
    }
}
