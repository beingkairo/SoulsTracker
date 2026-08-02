using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class LiesOfPBossCatalogTests
{
    [Fact]
    public void CatalogHasDeterministicStableIdsMembershipAndOrder()
    {
        BossDefinition[] bosses = GameCatalog.GetRequired(GameId.LiesOfP).BossCatalog.ToArray();

        Assert.Equal(33, bosses.Length);
        Assert.Equal(25, bosses.Count(static boss => boss.DlcLabel is null));
        Assert.Equal(8, bosses.Count(static boss => boss.DlcLabel == "Overture"));
        Assert.Equal("lop_base_001", bosses[0].Id.Value);
        Assert.Equal("Parade Master", bosses[0].DisplayName);
        Assert.Equal("lop_base_025", bosses[24].Id.Value);
        Assert.Equal("Nameless Puppet", bosses[24].DisplayName);
        Assert.Equal("lop_overture_001", bosses[25].Id.Value);
        Assert.Equal("Tyrannical Predator", bosses[25].DisplayName);
        Assert.Equal("lop_overture_008", bosses[^1].Id.Value);
        Assert.Equal("Arlecchino, the Blood Artist", bosses[^1].DisplayName);
        Assert.Equal(bosses.Length, bosses.Select(static boss => boss.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(BossListScope.AllBosses, 33)]
    [InlineData(BossListScope.MainGame, 25)]
    [InlineData(BossListScope.Dlc, 8)]
    public void DisplayFilterHasTheExpectedScope(BossListScope scope, int expectedCount)
    {
        BossDefinition[] filtered = BossCatalogDisplayFilter.Apply(
            GameCatalog.GetRequired(GameId.LiesOfP), scope).ToArray();

        Assert.Equal(expectedCount, filtered.Length);
        Assert.All(filtered, boss =>
        {
            if (scope == BossListScope.MainGame) Assert.Null(boss.DlcLabel);
            if (scope == BossListScope.Dlc) Assert.Equal("Overture", boss.DlcLabel);
        });
    }
}
