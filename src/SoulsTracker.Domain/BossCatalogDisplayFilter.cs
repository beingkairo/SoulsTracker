namespace SoulsTracker.Domain;

/// <summary>Applies persisted display filtering without changing canonical catalogs or progress.</summary>
public static class BossCatalogDisplayFilter
{
    /// <summary>Returns a scope that can produce a truthful catalog for the supplied game.</summary>
    public static BossListScope NormalizeScope(GameDefinition game, BossListScope scope)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!Enum.IsDefined(scope)) return BossListScope.AllBosses;

        return scope == BossListScope.Dlc && !game.BossCatalog.Any(static boss => boss.DlcLabel is not null)
            ? BossListScope.AllBosses
            : scope;
    }

    public static IEnumerable<BossDefinition> Apply(GameDefinition game, BossListScope scope)
    {
        ArgumentNullException.ThrowIfNull(game);
        scope = NormalizeScope(game, scope);

        return scope switch
        {
            BossListScope.AllBosses => game.BossCatalog,
            BossListScope.MainGame => game.BossCatalog.Where(static boss => boss.DlcLabel is null),
            BossListScope.Dlc => game.BossCatalog.Where(static boss => boss.DlcLabel is not null),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
    }
}
