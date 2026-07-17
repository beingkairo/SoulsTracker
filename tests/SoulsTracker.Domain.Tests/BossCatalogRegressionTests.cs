using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class BossCatalogRegressionTests
{
    private static readonly ExpectedCatalog[] ExpectedTrackedCatalogs =
    [
        new(
            GameId.Ds1,
            [
                Boss("asylum_demon", "Asylum Demon"),
                Boss("taurus_demon", "Taurus Demon"),
                Boss("bell_gargoyles", "Bell Gargoyles"),
                Boss("moonlight_butterfly", "Moonlight Butterfly"),
                Boss("capra_demon", "Capra Demon"),
                Boss("gaping_dragon", "Gaping Dragon"),
                Boss("chaos_witch_quelaag", "Chaos Witch Quelaag"),
                Boss("ceaseless_discharge", "Ceaseless Discharge"),
                Boss("iron_golem", "Iron Golem"),
                Boss("crossbreed_priscilla", "Crossbreed Priscilla"),
                Boss("ornstein_smough", "Ornstein and Smough"),
                Boss("dark_sun_gwyndolin", "Dark Sun Gwyndolin"),
                Boss("great_grey_wolf_sif", "Great Grey Wolf Sif"),
                Boss("four_kings", "Four Kings"),
                Boss("pinwheel", "Pinwheel"),
                Boss("gravelord_nito", "Gravelord Nito"),
                Boss("seath", "Seath the Scaleless"),
                Boss("stray_demon", "Stray Demon"),
                Boss("demon_firesage", "Demon Firesage"),
                Boss("centipede_demon", "Centipede Demon"),
                Boss("bed_of_chaos", "The Bed of Chaos"),
                Boss("sanctuary_guardian", "Sanctuary Guardian", "Artorias of the Abyss"),
                Boss("artorias", "Artorias the Abysswalker", "Artorias of the Abyss"),
                Boss("black_dragon_kalameet", "Black Dragon Kalameet", "Artorias of the Abyss"),
                Boss("manus", "Manus, Father of the Abyss", "Artorias of the Abyss"),
                Boss("gwyn", "Gwyn, Lord of Cinder"),
            ]),
        new(
            GameId.Ds2,
            [
                Boss("last_giant", "The Last Giant"),
                Boss("pursuer", "The Pursuer"),
                Boss("dragonrider", "Dragonrider"),
                Boss("old_dragonslayer", "Old Dragonslayer"),
                Boss("flexile_sentry", "Flexile Sentry"),
                Boss("ruin_sentinels", "Ruin Sentinels"),
                Boss("belfry_gargoyles", "Belfry Gargoyles"),
                Boss("lost_sinner", "The Lost Sinner"),
                Boss("skeleton_lords", "Skeleton Lords"),
                Boss("executioners_chariot", "Executioner's Chariot"),
                Boss("covetous_demon", "Covetous Demon"),
                Boss("mytha", "Mytha, the Baneful Queen"),
                Boss("smelter_demon", "Smelter Demon"),
                Boss("old_iron_king", "Old Iron King"),
                Boss("scorpioness_najka", "Scorpioness Najka"),
                Boss("royal_rat_vanguard", "Royal Rat Vanguard"),
                Boss("royal_rat_authority", "Royal Rat Authority"),
                Boss("the_rotten", "The Rotten"),
                Boss("prowling_magus", "Prowling Magus and Congregation"),
                Boss("dukes_dear_freja", "Duke's Dear Freja"),
                Boss("twin_dragonriders", "Twin Dragonriders"),
                Boss("looking_glass_knight", "Looking Glass Knight"),
                Boss("demon_of_song", "Demon of Song"),
                Boss("velstadt", "Velstadt, the Royal Aegis"),
                Boss("vendrick", "Vendrick"),
                Boss("guardian_dragon", "Guardian Dragon"),
                Boss("ancient_dragon", "Ancient Dragon"),
                Boss("giant_lord", "Giant Lord"),
                Boss("throne_duo", "Throne Watcher and Throne Defender"),
                Boss("nashandra", "Nashandra"),
                Boss("aldia", "Aldia, Scholar of the First Sin"),
                Boss("darklurker", "Darklurker"),
                Boss("elana", "Elana, Squalid Queen", "Crown of the Sunken King"),
                Boss("sinh", "Sinh, the Slumbering Dragon", "Crown of the Sunken King"),
                Boss(
                    "graverobber_varg_cerah",
                    "Afflicted Graverobber, Ancient Soldier Varg, and Cerah the Old Explorer",
                    "Crown of the Sunken King"),
                Boss("blue_smelter_demon", "Blue Smelter Demon", "Crown of the Old Iron King"),
                Boss("fume_knight", "Fume Knight", "Crown of the Old Iron King"),
                Boss("sir_alonne", "Sir Alonne", "Crown of the Old Iron King"),
                Boss("aava", "Aava, the King's Pet", "Crown of the Ivory King"),
                Boss("burnt_ivory_king", "Burnt Ivory King", "Crown of the Ivory King"),
                Boss("lud_zallen", "Lud and Zallen, the King's Pets", "Crown of the Ivory King"),
            ]),
        new(
            GameId.Ds3,
            [
                Boss("iudex_gundyr", "Iudex Gundyr"),
                Boss("vordt", "Vordt of the Boreal Valley"),
                Boss("curse_rotted_greatwood", "Curse-Rotted Greatwood"),
                Boss("crystal_sage", "Crystal Sage"),
                Boss("deacons_of_the_deep", "Deacons of the Deep"),
                Boss("abyss_watchers", "Abyss Watchers"),
                Boss("high_lord_wolnir", "High Lord Wolnir"),
                Boss("old_demon_king", "Old Demon King"),
                Boss("pontiff_sulyvahn", "Pontiff Sulyvahn"),
                Boss("yhorm", "Yhorm the Giant"),
                Boss("aldrich", "Aldrich, Devourer of Gods"),
                Boss("dancer", "Dancer of the Boreal Valley"),
                Boss("dragonslayer_armour", "Dragonslayer Armour"),
                Boss("oceiros", "Oceiros, the Consumed King"),
                Boss("champion_gundyr", "Champion Gundyr"),
                Boss("ancient_wyvern", "Ancient Wyvern"),
                Boss("nameless_king", "The Nameless King"),
                Boss("twin_princes", "Lorian and Lothric"),
                Boss("soul_of_cinder", "Soul of Cinder"),
                Boss(
                    "champions_gravetender",
                    "Champion's Gravetender and Gravetender Greatwolf",
                    "Ashes of Ariandel"),
                Boss("sister_friede", "Sister Friede", "Ashes of Ariandel"),
                Boss("demon_prince", "Demon Prince", "The Ringed City"),
                Boss("halflight", "Halflight, Spear of the Church", "The Ringed City"),
                Boss("darkeater_midir", "Darkeater Midir", "The Ringed City"),
                Boss("slave_knight_gael", "Slave Knight Gael", "The Ringed City"),
            ]),
        new(
            GameId.Sekiro,
            [
                Boss("gyoubu_oniwa", "Gyoubu Masataka Oniwa"),
                Boss("lady_butterfly", "Lady Butterfly"),
                Boss("genichiro_ashina", "Genichiro Ashina"),
                Boss("folding_screen_monkeys", "Folding Screen Monkeys"),
                Boss("guardian_ape", "Guardian Ape"),
                Boss("headless_ape", "Headless Ape"),
                Boss("corrupted_monk", "Corrupted Monk"),
                Boss("great_shinobi_owl", "Great Shinobi - Owl"),
                Boss("emma", "Emma, the Gentle Blade"),
                Boss("isshin_ashina", "Isshin Ashina"),
                Boss("true_monk", "True Monk"),
                Boss("divine_dragon", "Divine Dragon"),
                Boss("owl_father", "Owl (Father)"),
                Boss("demon_of_hatred", "Demon of Hatred"),
                Boss("genichiro_way_of_tomoe", "Genichiro, Way of Tomoe"),
                Boss("isshin_sword_saint", "Isshin, the Sword Saint"),
            ]),
        new(
            GameId.Bloodborne,
            [
                Boss("cleric_beast", "Cleric Beast"),
                Boss("father_gascoigne", "Father Gascoigne"),
                Boss("blood_starved_beast", "Blood-starved Beast"),
                Boss("vicar_amelia", "Vicar Amelia"),
                Boss("witch_of_hemwick", "Witch of Hemwick"),
                Boss("shadow_of_yharnam", "Shadow of Yharnam"),
                Boss("rom", "Rom, the Vacuous Spider"),
                Boss("darkbeast_paarl", "Darkbeast Paarl"),
                Boss("amygdala", "Amygdala"),
                Boss("martyr_logarius", "Martyr Logarius"),
                Boss("the_one_reborn", "The One Reborn"),
                Boss("celestial_emissary", "Celestial Emissary"),
                Boss("ebrietas", "Ebrietas, Daughter of the Cosmos"),
                Boss("micolash", "Micolash, Host of the Nightmare"),
                Boss("mergos_wet_nurse", "Mergo's Wet Nurse"),
                Boss("gehrman", "Gehrman, the First Hunter"),
                Boss("moon_presence", "Moon Presence"),
                Boss("ludwig", "Ludwig, the Holy Blade"),
                Boss("laurence", "Laurence, the First Vicar"),
                Boss("living_failures", "Living Failures"),
                Boss("lady_maria", "Lady Maria of the Astral Clocktower"),
                Boss("orphan_of_kos", "Orphan of Kos"),
            ]),
    ];

    [Fact]
    public void TrackedBossCatalogsMatchTheCompleteApprovedOrderedFixture()
    {
        foreach (ExpectedCatalog expectedCatalog in ExpectedTrackedCatalogs)
        {
            ExpectedBoss[] actual = GameCatalog.GetRequired(expectedCatalog.GameId).BossCatalog
                .Select(static boss => new ExpectedBoss(boss.Id.Value, boss.DisplayName, boss.DlcLabel))
                .ToArray();

            Assert.Equal(expectedCatalog.Bosses, actual);
            Assert.Equal(
                actual.Length,
                actual.Select(static boss => boss.Id).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void BloodborneOldHuntersBossesRetainTheirIdentityWithoutTheRedundantSubtitle()
    {
        BossDefinition[] oldHuntersBosses = GameCatalog.GetRequired(GameId.Bloodborne).BossCatalog
            .Where(static boss => boss.Id.Value is "ludwig" or "laurence" or "living_failures" or "lady_maria" or "orphan_of_kos")
            .ToArray();

        Assert.Equal(5, oldHuntersBosses.Length);
        Assert.All(oldHuntersBosses, static boss => Assert.Null(boss.DlcLabel));
        Assert.DoesNotContain(GameCatalog.GetRequired(GameId.Bloodborne).BossCatalog, static boss =>
            string.Equals(boss.DlcLabel, "The Old Hunters", StringComparison.Ordinal));
    }

    private static ExpectedBoss Boss(string id, string displayName, string? dlcLabel = null) =>
        new(id, displayName, dlcLabel);

    private sealed record ExpectedCatalog(GameId GameId, IReadOnlyList<ExpectedBoss> Bosses);

    private sealed record ExpectedBoss(string Id, string DisplayName, string? DlcLabel);
}
