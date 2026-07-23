namespace SoulsTracker.Domain;

/// <summary>Applies persisted display filtering without changing canonical catalogs or progress.</summary>
public static class BossCatalogDisplayFilter
{
    public static IEnumerable<BossDefinition> Apply(GameDefinition game, EldenRingSaveConfiguration eldenRingSave)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(eldenRingSave);

        if (game.Id != GameId.EldenRing)
        {
            return game.BossCatalog;
        }

        IEnumerable<BossDefinition> bosses = eldenRingSave.BossListScope switch
        {
            EldenRingBossListScope.AllBosses => game.BossCatalog,
            EldenRingBossListScope.BaseGame => game.BossCatalog.Where(static boss => boss.DlcLabel is null),
            EldenRingBossListScope.ShadowOfTheErdtree => game.BossCatalog.Where(static boss => boss.DlcLabel == EldenRingBossCatalog.ShadowOfTheErdtree),
            _ => throw new ArgumentOutOfRangeException(nameof(eldenRingSave)),
        };

        return eldenRingSave.RequiredBossesOnly
            ? bosses.Where(static boss => boss.IsProgressionRequired)
            : bosses;
    }
}
