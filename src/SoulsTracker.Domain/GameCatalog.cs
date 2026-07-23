using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace SoulsTracker.Domain;

/// <summary>
/// Provides the canonical V1 game and boss definitions. Lookups use exact IDs
/// and never substitute a default game or catalog.
/// </summary>
public static class GameCatalog
{
    private static readonly ReadOnlyCollection<GameDefinition> Definitions = Array.AsReadOnly(
    [
        // Catalog order follows each game's original release, not a remaster,
        // re-release, or enhanced-edition date. The desktop game picker uses
        // this stable canonical order directly.
        new GameDefinition(
            GameId.DemonsSouls,
            "Demon Souls",
            GameUiAvailability.Selectable,
            GameTrackingMode.ManualOnly,
            ReaderBindingState.IntentionallyUnavailable,
            [
                Boss("phalanx", "Phalanx"), Boss("tower_knight", "Tower Knight"), Boss("penetrator", "Penetrator"), Boss("false_king_allant", "False King Allant"),
                Boss("armor_spider", "Armor Spider"), Boss("flamelurker", "Flamelurker"), Boss("dragon_god", "Dragon God"), Boss("fools_idol", "Fool's Idol"),
                Boss("maneater", "Maneater"), Boss("old_monk", "Old Monk"), Boss("adjudicator", "Adjudicator"), Boss("old_hero", "Old Hero"),
                Boss("storm_king", "Storm King"), Boss("leechmonger", "Leechmonger"), Boss("dirty_colossus", "Dirty Colossus"), Boss("maiden_astraea", "Maiden Astraea"),
            ]),
        new GameDefinition(
            GameId.Ds1,
            "Dark Souls: Remastered",
            GameUiAvailability.Selectable,
            GameTrackingMode.GameLifetimeReadOnly,
            ReaderBindingState.PendingVerification,
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
        new GameDefinition(
            GameId.Ds2,
            "Dark Souls II: Scholar of the First Sin",
            GameUiAvailability.Selectable,
            GameTrackingMode.GameLifetimeReadOnly,
            ReaderBindingState.PendingVerification,
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
        new GameDefinition(
            GameId.Ds3,
            "Dark Souls III",
            GameUiAvailability.Selectable,
            GameTrackingMode.GameLifetimeReadOnly,
            ReaderBindingState.PendingVerification,
            [
                Boss("iudex_gundyr", "Iudex Gundyr"), Boss("vordt", "Vordt of the Boreal Valley"), Boss("curse_rotted_greatwood", "Curse-Rotted Greatwood"),
                Boss("crystal_sage", "Crystal Sage"), Boss("deacons_of_the_deep", "Deacons of the Deep"), Boss("abyss_watchers", "Abyss Watchers"),
                Boss("high_lord_wolnir", "High Lord Wolnir"), Boss("old_demon_king", "Old Demon King"), Boss("pontiff_sulyvahn", "Pontiff Sulyvahn"),
                Boss("yhorm", "Yhorm the Giant"), Boss("aldrich", "Aldrich, Devourer of Gods"), Boss("dancer", "Dancer of the Boreal Valley"),
                Boss("dragonslayer_armour", "Dragonslayer Armour"), Boss("oceiros", "Oceiros, the Consumed King"), Boss("champion_gundyr", "Champion Gundyr"),
                Boss("ancient_wyvern", "Ancient Wyvern"), Boss("nameless_king", "The Nameless King"), Boss("twin_princes", "Lorian and Lothric"),
                Boss("soul_of_cinder", "Soul of Cinder"), Boss("champions_gravetender", "Champion's Gravetender and Gravetender Greatwolf", "Ashes of Ariandel"),
                Boss("sister_friede", "Sister Friede", "Ashes of Ariandel"), Boss("demon_prince", "Demon Prince", "The Ringed City"),
                Boss("halflight", "Halflight, Spear of the Church", "The Ringed City"), Boss("darkeater_midir", "Darkeater Midir", "The Ringed City"),
                Boss("slave_knight_gael", "Slave Knight Gael", "The Ringed City"),
            ]),
        new GameDefinition(
            GameId.Bloodborne,
            "Bloodborne",
            GameUiAvailability.Selectable,
            GameTrackingMode.ManualOnly,
            ReaderBindingState.IntentionallyUnavailable,
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
        new GameDefinition(
            GameId.Sekiro,
            "Sekiro: Shadows Die Twice",
            GameUiAvailability.Selectable,
            GameTrackingMode.GameLifetimeReadOnly,
            ReaderBindingState.PendingVerification,
            [
                Boss("gyoubu_oniwa", "Gyoubu Masataka Oniwa"), Boss("lady_butterfly", "Lady Butterfly"), Boss("genichiro_ashina", "Genichiro Ashina"),
                Boss("folding_screen_monkeys", "Folding Screen Monkeys"), Boss("guardian_ape", "Guardian Ape"), Boss("headless_ape", "Headless Ape"),
                Boss("corrupted_monk", "Corrupted Monk"), Boss("great_shinobi_owl", "Great Shinobi - Owl"), Boss("emma", "Emma, the Gentle Blade"),
                Boss("isshin_ashina", "Isshin Ashina"), Boss("true_monk", "True Monk"), Boss("divine_dragon", "Divine Dragon"), Boss("owl_father", "Owl (Father)"),
                Boss("demon_of_hatred", "Demon of Hatred"), Boss("genichiro_way_of_tomoe", "Genichiro, Way of Tomoe"), Boss("isshin_sword_saint", "Isshin, the Sword Saint"),
            ]),
        new GameDefinition(
            GameId.EldenRing,
            "Elden Ring",
            GameUiAvailability.Selectable,
            GameTrackingMode.GameLifetimeReadOnly,
            ReaderBindingState.PendingVerification),
    ]);

    private static readonly ReadOnlyDictionary<GameId, GameDefinition> DefinitionsById =
        new ReadOnlyDictionary<GameId, GameDefinition>(
            Definitions.ToDictionary(static definition => definition.Id, static definition => definition));

    /// <summary>
    /// Gets every canonical game definition in stable catalog order.
    /// </summary>
    public static IReadOnlyList<GameDefinition> All => Definitions;

    /// <summary>
    /// Returns a definition for a known canonical game ID.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="gameId"/> is unknown.</exception>
    public static GameDefinition GetRequired(GameId gameId)
    {
        ArgumentNullException.ThrowIfNull(gameId);

        if (DefinitionsById.TryGetValue(gameId, out GameDefinition? definition))
        {
            return definition;
        }

        throw new ArgumentException("The game ID is not in the canonical catalog.", nameof(gameId));
    }

    /// <summary>
    /// Parses and returns an exact canonical game definition without a fallback.
    /// </summary>
    public static GameDefinition GetRequired(string gameId) => GetRequired(GameId.Parse(gameId));

    /// <summary>
    /// Attempts to find an exact canonical game definition without normalization.
    /// </summary>
    public static bool TryGet(string? gameId, [NotNullWhen(true)] out GameDefinition? definition)
    {
        if (GameId.TryParse(gameId, out GameId? parsedGameId) &&
            DefinitionsById.TryGetValue(parsedGameId, out GameDefinition? knownDefinition))
        {
            definition = knownDefinition;
            return true;
        }

        definition = null;
        return false;
    }

    /// <summary>
    /// Returns a boss only when it belongs to the supplied game's catalog.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the game or boss is unknown or mismatched.</exception>
    public static BossDefinition GetRequiredBoss(GameId gameId, BossId bossId) =>
        GetRequired(gameId).GetRequiredBoss(bossId);

    private static BossDefinition Boss(string id, string displayName, string? dlcLabel = null) =>
        new(BossId.Parse(id), displayName, dlcLabel);
}
