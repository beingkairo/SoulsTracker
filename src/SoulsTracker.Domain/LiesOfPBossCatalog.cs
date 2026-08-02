namespace SoulsTracker.Domain;

/// <summary>Canonical base-game and Overture encounter checklist for Lies of P.</summary>
public static class LiesOfPBossCatalog
{
    private const string Overture = "Overture";

    /// <remarks>
    /// The base-game encounters are followed by the Overture expansion in encounter
    /// order. Multi-phase encounters remain a single checklist entry.
    /// </remarks>
    public static IReadOnlyList<BossDefinition> Create() =>
    [
        Boss("lop_base_001", "Parade Master"),
        Boss("lop_base_002", "Mad Donkey"),
        Boss("lop_base_003", "Scrapped Watchman"),
        Boss("lop_base_004", "Survivor"),
        Boss("lop_base_005", "Puppet of the Future"),
        Boss("lop_base_006", "King's Flame, Fuoco"),
        Boss("lop_base_007", "The Atoned"),
        Boss("lop_base_008", "Fallen Archbishop Andreus"),
        Boss("lop_base_009", "Eldest of the Black Rabbit Brotherhood"),
        Boss("lop_base_010", "The White Lady"),
        Boss("lop_base_011", "Mad Clown Puppet"),
        Boss("lop_base_012", "King of Puppets / Romeo, King of Puppets"),
        Boss("lop_base_013", "Champion Victor"),
        Boss("lop_base_014", "Owl Doctor"),
        Boss("lop_base_015", "Green Monster of the Swamp / Puppet-Devouring Green Monster"),
        Boss("lop_base_016", "Robber Weasel"),
        Boss("lop_base_017", "Walker of Illusions"),
        Boss("lop_base_018", "Corrupted Parade Master"),
        Boss("lop_base_019", "Black Rabbit Brotherhood"),
        Boss("lop_base_020", "Door Guardian"),
        Boss("lop_base_021", "Black Cat"),
        Boss("lop_base_022", "Laxasia the Complete"),
        Boss("lop_base_023", "Red Fox"),
        Boss("lop_base_024", "Simon Manus, Arm of God / Simon Manus, Awakened God"),
        Boss("lop_base_025", "Nameless Puppet"),
        Boss("lop_overture_001", "Tyrannical Predator", Overture),
        Boss("lop_overture_002", "Markiona, Puppeteer of Death", Overture),
        Boss("lop_overture_003", "Véronique, Leader of the Sweepers", Overture),
        Boss("lop_overture_004", "Two-Faced Overseer", Overture),
        Boss("lop_overture_005", "Premetamorphic Green Hunter", Overture),
        Boss("lop_overture_006", "Anguished Guardian of the Ruins", Overture),
        Boss("lop_overture_007", "Lumacchio, Leader of the Bastards", Overture),
        Boss("lop_overture_008", "Arlecchino, the Blood Artist", Overture),
    ];

    private static BossDefinition Boss(string id, string displayName, string? dlcLabel = null) =>
        new(BossId.Parse(id), displayName, dlcLabel);
}
