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
        Assert.Equal("Radagon / Elden Beast", bosses[164].DisplayName);
        Assert.Equal("er_sote_001", bosses[165].Id.Value);
        Assert.Equal("Blackgaol Knight", bosses[165].DisplayName);
        Assert.Equal("er_sote_042", bosses[^1].Id.Value);
        Assert.Equal("Needle Knight Leda and Allies", bosses[^1].DisplayName);
        Assert.Equal(bosses.Length, bosses.Select(static boss => boss.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(BossListScope.AllBosses, 207)]
    [InlineData(BossListScope.MainGame, 165)]
    [InlineData(BossListScope.Dlc, 42)]
    public void DisplayFilterHasTheExpectedScope(
        BossListScope scope,
        int expectedCount)
    {
        GameDefinition game = GameCatalog.GetRequired(GameId.EldenRing);
        BossDefinition[] filtered = BossCatalogDisplayFilter.Apply(
            game, scope).ToArray();

        Assert.Equal(expectedCount, filtered.Length);
        Assert.All(filtered, boss =>
        {
            if (scope == BossListScope.MainGame) Assert.Null(boss.DlcLabel);
            if (scope == BossListScope.Dlc) Assert.Equal("Shadow of the Erdtree", boss.DlcLabel);
        });
    }

    [Fact]
    public void CatalogUsesApprovedShortNamesWithoutMakingGodfreyOrRadahnAmbiguous()
    {
        BossDefinition[] bosses = GameCatalog.GetRequired(GameId.EldenRing).BossCatalog.ToArray();
        string[] approvedNames =
        [
            "Godrick", "Rennala", "Radahn (Starscourge)", "God-Devouring Serpent / Rykard", "Mohg", "Malenia",
            "Godfrey (Golden Shade)", "Morgott", "Beast Clergyman / Maliketh", "Gideon Ofnir", "Godfrey (Hoarah Loux)",
            "Radagon / Elden Beast", "Rellana", "Messmer", "Romina", "Radahn (Promised Consort)",
        ];

        Assert.All(approvedNames, expected => Assert.Contains(bosses, boss => boss.DisplayName == expected));
        Assert.Equal(2, bosses.Count(static boss => boss.DisplayName.StartsWith("Godfrey (", StringComparison.Ordinal)));
        Assert.Equal(2, bosses.Count(static boss => boss.DisplayName.StartsWith("Radahn (", StringComparison.Ordinal)));
    }
}
