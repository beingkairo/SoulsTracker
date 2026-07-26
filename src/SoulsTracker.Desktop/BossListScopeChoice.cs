using SoulsTracker.Domain;

namespace SoulsTracker.Desktop;

public sealed record BossListScopeChoice(BossListScope Value, string Label, bool IsAvailable)
{
    public static IReadOnlyList<BossListScopeChoice> For(GameDefinition game) =>
    [
        new(BossListScope.AllBosses, "All bosses", true),
        new(BossListScope.MainGame, "Main game", true),
        new(BossListScope.Dlc, game.BossCatalog.Any(static boss => boss.DlcLabel is not null) ? "DLC" : "DLC (none)", game.BossCatalog.Any(static boss => boss.DlcLabel is not null)),
    ];
}
