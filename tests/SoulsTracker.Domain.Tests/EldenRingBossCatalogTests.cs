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
    public void CatalogRestoresCanonicalBossTitlesWithoutAddingLocationNoise()
    {
        BossDefinition[] bosses = GameCatalog.GetRequired(GameId.EldenRing).BossCatalog.ToArray();
        string[] expectedNames =
        [
            "Godrick the Grafted", "Rennala, Queen of the Full Moon", "Starscourge Radahn",
            "God-Devouring Serpent / Rykard, Lord of Blasphemy", "Mohg, Lord of Blood", "Malenia, Blade of Miquella",
            "Godfrey, First Elden Lord (Golden Shade)", "Morgott, the Omen King", "Beast Clergyman / Maliketh, the Black Blade",
            "Sir Gideon Ofnir, the All-Knowing", "Godfrey, First Elden Lord (Hoarah Loux)",
            "Radagon of the Golden Order / Elden Beast", "Rellana, Twin Moon Knight", "Messmer the Impaler",
            "Romina, Saint of the Bud", "Promised Consort Radahn", "Curseblade Labirith",
        ];

        Assert.All(expectedNames, expected => Assert.Contains(bosses, boss => boss.DisplayName == expected));
        Assert.DoesNotContain(bosses, static boss => boss.DisplayName == "Godrick");
        Assert.DoesNotContain(bosses, static boss => boss.DisplayName == "Rellana");
        Assert.DoesNotContain(bosses, static boss => boss.DisplayName == "Radahn (Promised Consort)");
    }
}
