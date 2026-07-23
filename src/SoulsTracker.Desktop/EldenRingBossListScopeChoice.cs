using SoulsTracker.Domain;

namespace SoulsTracker.Desktop;

public sealed record EldenRingBossListScopeChoice(EldenRingBossListScope Value, string Label)
{
    public static IReadOnlyList<EldenRingBossListScopeChoice> All { get; } =
    [
        new(EldenRingBossListScope.AllBosses, "All bosses"),
        new(EldenRingBossListScope.BaseGame, "Base game"),
        new(EldenRingBossListScope.ShadowOfTheErdtree, "Shadow of the Erdtree"),
    ];
}
