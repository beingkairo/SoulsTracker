using SoulsTracker.Domain;

namespace SoulsTracker.Desktop;

public sealed record BossListScopeChoice(BossListScope Value, string Label, bool IsAvailable)
{
    public static IReadOnlyList<BossListScopeChoice> For(GameDefinition game)
    {
        ArgumentNullException.ThrowIfNull(game);

        return game.BossCatalog.Any(static boss => boss.DlcLabel is not null)
            ?
            [
                new(BossListScope.AllBosses, "All bosses", true),
                new(BossListScope.MainGame, "Main game", true),
                new(BossListScope.Dlc, "DLC", true),
            ]
            :
            [new(BossListScope.AllBosses, "All bosses", true)];
    }
}
