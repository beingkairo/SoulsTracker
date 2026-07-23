using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class EldenRingBossCatalogTests
{
    [Fact]
    public void CatalogHasDeterministicStableIdsMembershipAndOrder()
    {
        BossDefinition[] bosses = GameCatalog.GetRequired(GameId.EldenRing).BossCatalog.ToArray();

        Assert.Equal(207, bosses.Length);
        Assert.Equal(165, bosses.Count(static boss => boss.DlcLabel is null));
        Assert.Equal(42, bosses.Count(static boss => boss.DlcLabel == "Shadow of the Erdtree"));
        Assert.Equal("er_base_001", bosses[0].Id.Value);
        Assert.Equal("Ancient Hero of Zamor (Weeping Evergaol)", bosses[0].DisplayName);
        Assert.Equal("er_base_165", bosses[164].Id.Value);
        Assert.Equal("Radagon of the Golden Order / Elden Beast", bosses[164].DisplayName);
        Assert.Equal("er_sote_001", bosses[165].Id.Value);
        Assert.Equal("Blackgaol Knight", bosses[165].DisplayName);
        Assert.Equal("er_sote_042", bosses[^1].Id.Value);
        Assert.Equal("Needle Knight Leda and Allies", bosses[^1].DisplayName);
        Assert.Equal(bosses.Length, bosses.Select(static boss => boss.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(EldenRingBossListScope.AllBosses, false, 207)]
    [InlineData(EldenRingBossListScope.BaseGame, false, 165)]
    [InlineData(EldenRingBossListScope.ShadowOfTheErdtree, false, 42)]
    [InlineData(EldenRingBossListScope.AllBosses, true, 20)]
    [InlineData(EldenRingBossListScope.BaseGame, true, 14)]
    [InlineData(EldenRingBossListScope.ShadowOfTheErdtree, true, 6)]
    public void DisplayFilterHasTheExpectedScopeAndRequiredBossMatrix(
        EldenRingBossListScope scope,
        bool requiredOnly,
        int expectedCount)
    {
        GameDefinition game = GameCatalog.GetRequired(GameId.EldenRing);
        BossDefinition[] filtered = BossCatalogDisplayFilter.Apply(
            game,
            new EldenRingSaveConfiguration(null, 0, scope, requiredOnly)).ToArray();

        Assert.Equal(expectedCount, filtered.Length);
        Assert.All(filtered, boss =>
        {
            if (scope == EldenRingBossListScope.BaseGame) Assert.Null(boss.DlcLabel);
            if (scope == EldenRingBossListScope.ShadowOfTheErdtree) Assert.Equal("Shadow of the Erdtree", boss.DlcLabel);
            if (requiredOnly) Assert.True(boss.IsProgressionRequired);
        });
    }

    [Fact]
    public void RequiredMembershipIncludesDocumentedRouteCandidatesAndFinalGates()
    {
        GameDefinition game = GameCatalog.GetRequired(GameId.EldenRing);
        BossDefinition[] required = BossCatalogDisplayFilter.Apply(
            game,
            new EldenRingSaveConfiguration(null, 0, EldenRingBossListScope.AllBosses, true)).ToArray();

        Assert.Contains(required, static boss => boss.DisplayName == "Godrick the Grafted");
        Assert.Contains(required, static boss => boss.DisplayName == "Godskin Duo");
        Assert.Contains(required, static boss => boss.DisplayName == "Radagon of the Golden Order / Elden Beast");
        Assert.Contains(required, static boss => boss.DisplayName == "Promised Consort Radahn");
    }
}
