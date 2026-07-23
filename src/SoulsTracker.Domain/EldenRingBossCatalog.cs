namespace SoulsTracker.Domain;

/// <summary>
/// The manual Elden Ring checklist. IDs are deliberately local, stable sequence
/// IDs; they are not borrowed from a game file, mod, or external checklist.
/// </summary>
internal static class EldenRingBossCatalog
{
    internal const string ShadowOfTheErdtree = "Shadow of the Erdtree";

    private static readonly HashSet<string> RequiredBaseGameNames = new(StringComparer.Ordinal)
    {
        "Godrick the Grafted", "Rennala, Queen of the Full Moon", "Starscourge Radahn",
        "God-Devouring Serpent / Rykard, Lord of Blasphemy", "Mohg, Lord of Blood", "Malenia, Blade of Miquella",
        "Godfrey, First Elden Lord (Golden Shade)", "Morgott, the Omen King", "Fire Giant",
        "Godskin Duo", "Beast Clergyman / Maliketh, the Black Blade", "Sir Gideon Ofnir, the All-Knowing",
        "Godfrey, First Elden Lord (Hoarah Loux)", "Radagon of the Golden Order / Elden Beast",
    };

    private static readonly HashSet<string> RequiredShadowOfTheErdtreeNames = new(StringComparer.Ordinal)
    {
        "Divine Beast Dancing Lion", "Rellana Twin Moon Knight", "Golden Hippopotamus",
        "Messmer the Impaler", "Romina, Saint of the Bud", "Promised Consort Radahn",
    };

    internal static IReadOnlyList<BossDefinition> Create()
    {
        var bosses = new List<BossDefinition>(207);
        Add(bosses, BaseGameNames, "er_base", null, RequiredBaseGameNames);
        Add(bosses, ShadowOfTheErdtreeNames, "er_sote", ShadowOfTheErdtree, RequiredShadowOfTheErdtreeNames);
        return bosses;
    }

    private static void Add(
        List<BossDefinition> destination,
        IReadOnlyList<string> names,
        string prefix,
        string? dlcLabel,
        HashSet<string> requiredNames)
    {
        for (int index = 0; index < names.Count; index++)
        {
            string name = names[index];
            destination.Add(new BossDefinition(
                BossId.Parse($"{prefix}_{index + 1:D3}"),
                name,
                dlcLabel,
                requiredNames.Contains(name)));
        }
    }

    // One checklist entry means one encounter. Multi-phase encounters are one
    // entry; repeated encounters use their in-game/location qualifier where known.
    private static IReadOnlyList<string> BaseGameNames { get; } =
    [
        "Ancient Hero of Zamor (Weeping Evergaol)", "Beastman of Farum Azula", "Bell Bearing Hunter", "Black Knife Assassin",
        "Bloodhound Knight Darriwil", "Cemetery Shade", "Crucible Knight", "Deathbird (Limgrave)", "Deathbird (Weeping Peninsula)", "Demi-Human Chief",
        "Erdtree Avatar", "Erdtree Burial Watchdog (Stormfoot Catacombs)", "Erdtree Burial Watchdog (Impaler's Catacombs)", "Flying Dragon Agheel", "Godrick the Grafted",
        "Grafted Scion", "Grave Warden Duelist (Murkwater Catacombs)", "Guardian Golem", "Leonine Misbegotten", "Mad Pumpkin Head",
        "Margit, the Fell Omen", "Miranda the Blighted Bloom", "Night's Cavalry (Limgrave)", "Night's Cavalry (Weeping Peninsula)", "Patches",
        "Runebear", "Scaly Misbegotten", "Soldier of Godrick", "Stonedigger Troll", "Tibia Mariner", "Tree Sentinel", "Ulcerated Tree Spirit",
        "Adan, Thief of Fire", "Alecto, Black Knife Ringleader", "Bell Bearing Hunter (Liurnia)", "Black Knife Assassin (Deathtouched Catacombs)", "Bloodhound Knight",
        "Bols, Carian Knight", "Cemetery Shade (Black Knife Catacombs)", "Cleanrot Knight", "Crystalian (Spear) & Crystalian (Staff) (Academy Crystal Cave)",
        "Crystalian (Raya Lucaria Crystal Tunnel)", "Death Rite Bird (Liurnia)", "Deathbird (Liurnia of the Lakes)", "Erdtree Avatar (Northeast Liurnia)",
        "Erdtree Avatar (Southwest Liurnia)", "Erdtree Burial Watchdog (Cliffbottom Catacombs)", "Glintstone Dragon Adula", "Glintstone Dragon Smarag", "Magma Wyrm Makar",
        "Night's Cavalry (Liurnia North)", "Night's Cavalry (Liurnia South)", "Omenkiller", "Onyx Lord", "Red Wolf of Radagon",
        "Rennala, Queen of the Full Moon", "Royal Knight Loretta", "Royal Revenant", "Spirit-Caller Snail", "Tibia Mariner (Liurnia)",
        "Battlemage Hugues", "Beastman of Farum Azula (Dragonbarrow Cave)", "Bell Bearing Hunter (Caelid)", "Black Blade Kindred", "Cemetery Shade (Caelid Catacombs)",
        "Cleanrot Knight (Caelid)", "Commander O'Neil", "Crucible Knight & Misbegotten Warrior", "Death Rite Bird (Caelid)", "Decaying Ekzykes",
        "Erdtree Burial Watchdog (Minor Erdtree Catacombs)", "Fallingstar Beast", "Flying Dragon Greyll", "Frenzied Duelist", "Godskin Apostle (Divine Tower of Caelid)",
        "Mad Pumpkin Head (Duo)", "Magma Wyrm (Gael Tunnel)", "Night's Cavalry (Caelid Highway South)", "Night's Cavalry (Dragonbarrow East)", "Nox Swordstress & Nox Priest",
        "Putrid Avatar (West Caelid)", "Putrid Avatar (East Caelid)", "Putrid Tree Spirit", "Putrid Crystalian Trio", "Starscourge Radahn",
        "Ancient Hero of Zamor (Sainted Hero's Grave)", "Ancient Dragon Lansseax", "Black Knife Assassin (Sage's Cave)", "Black Knife Assassin (Sainted Hero's Grave)",
        "Crystalian Spear & Crystalian Ringblade", "Demi-Human Queen Gilika", "Elemer of the Briar", "Fallingstar Beast (Altus Plateau)", "Godefroy the Grafted",
        "Godskin Apostle (Windmill Village)", "Necromancer Garris", "Night's Cavalry (Altus Plateau)", "Omenkiller & Miranda the Blighted Bloom", "Perfumer Tricia & Misbegotten Warrior",
        "Sanguine Noble", "Stonedigger Troll (Old Altus Tunnel)", "Tibia Mariner (Wyndham Ruins)", "Tree Sentinel Duo", "Wormface",
        "Abductor Virgins", "Demi-Human Queen Margot", "Demi-Human Queen Maggie", "Erdtree Burial Watchdog (Wyndham Catacombs)", "Full-Grown Fallingstar Beast",
        "Godskin Noble", "Kindred of Rot", "Magma Wyrm (Seethewater Cave)", "Ulcerated Tree Spirit (Mt. Gelmir)", "God-Devouring Serpent / Rykard, Lord of Blasphemy",
        "Red Wolf of the Champion", "Bell Bearing Hunter (Capital Outskirts)", "Crucible Knight & Crucible Knight Ordovis", "Deathbird (Capital Outskirts)", "Draconic Tree Sentinel",
        "Esgar, Priest of Blood", "Fell Twins", "Grave Warden Duelist (Auriza Side Tomb)", "Godfrey, First Elden Lord (Golden Shade)", "Morgott, the Omen King",
        "Mohg, the Omen", "Onyx Lord (Sealed Tunnel)", "Ancient Hero of Zamor (Giant-Conquering Hero's Grave)", "Borealis the Freezing Fog", "Commander Niall",
        "Death Rite Bird (Mountaintops)", "Erdtree Avatar (Mountaintops)", "Fire Giant", "Godskin Apostle and Godskin Noble", "Roundtable Knight Vyke",
        "Ulcerated Tree Spirit (Giants' Mountaintop Catacombs)", "Ancestor Spirit", "Dragonkin Soldier", "Mohg, Lord of Blood", "Dragonkin Soldier of Nokstella",
        "Beast Clergyman / Maliketh, the Black Blade", "Dragonlord Placidusax", "Godskin Duo", "Black Blade Kindred (Bestial Sanctum)", "Night's Cavalry (Forbidden Lands)",
        "Mimic Tear", "Regal Ancestor Spirit", "Valiant Gargoyle & Valiant Gargoyle (Twinblade)", "Crucible Knight Siluria", "Fia's Champions",
        "Lichdragon Fortissax", "Astel, Naturalborn of the Void", "Dragonkin Soldier (Lake of Rot)", "Astel, Stars of Darkness", "Death Rite Bird (Consecrated Snowfield)",
        "Great Wyrm Theodorix", "Loretta, Knight of the Haligtree", "Malenia, Blade of Miquella", "Misbegotten Crusader", "Night's Cavalry (Consecrated Snowfield Duo)",
        "Putrid Avatar (Consecrated Snowfield)", "Putrid Grave Warden Duelist", "Stray Mimic Tear", "Godfrey, First Elden Lord (Hoarah Loux)",
        "Sir Gideon Ofnir, the All-Knowing", "Radagon of the Golden Order / Elden Beast",
    ];

    private static IReadOnlyList<string> ShadowOfTheErdtreeNames { get; } =
    [
        "Blackgaol Knight", "Chief Bloodfiend", "Demi-Human Swordmaster Onze", "Divine Beast Dancing Lion", "Ghostflame Dragon (Gravesite Plain)",
        "Lamenter", "Rellana Twin Moon Knight", "Red Bear", "Black Knight Edreed", "Black Knight Garrew", "Commander Gaius",
        "Count Ymir, Mother of Fingers", "Curseblade Labirith", "Death Knight (Fog Rift Catacombs)", "Dryleaf Dane", "Ghostflame Dragon (Cerulean Coast)",
        "Golden Hippopotamus", "Messmer the Impaler", "Metyr, Mother of Fingers", "Rakshasa", "Ralva the Great Red Bear", "Tree Sentinel (Duo)",
        "Death Knight (Scorpion River Catacombs)", "Divine Beast Dancing Lion (Rauh Ancient Ruins)", "Romina, Saint of the Bud", "Rugalea the Great Red Bear",
        "Dancer of Ranah", "Ghostflame Dragon (Jagged Peak)", "Putrescent Knight", "Death Rite Bird (Charo's Hidden Grave)", "Demi-Human Queen Marigga",
        "Ancient Dragon-Man", "Ancient Dragon Senessax", "Bayle the Dread", "Jagged Peak Drake (Lower)", "Jori, Elder Inquisitor",
        "Midra, Lord of Frenzied Flame", "Fallingstar Beast (Finger Ruins)", "Scadutree Avatar", "Promised Consort Radahn", "Jagged Peak Drake (Upper)",
        "Needle Knight Leda and Allies",
    ];
}
