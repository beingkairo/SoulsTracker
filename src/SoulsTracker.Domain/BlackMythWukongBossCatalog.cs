namespace SoulsTracker.Domain;

/// <summary>Canonical base-game encounter checklist for Black Myth: Wukong.</summary>
public static class BlackMythWukongBossCatalog
{
    /// <remarks>
    /// The 96 encounter groups follow the chapter ordering in the delivery research.
    /// Repeated fights are qualified by their encounter number; multi-fight sequences
    /// remain grouped where the source documents them as a single encounter.
    /// </remarks>
    public static IReadOnlyList<BossDefinition> Create() =>
    [
        // Chapter 1 — Black Wind Mountain
        Boss("c1_erlang_sacred_divinity", "Erlang, the Sacred Divinity"), Boss("c1_bullguard", "Bullguard"),
        Boss("c1_wandering_wight", "Wandering Wight"), Boss("c1_guangzhi", "Guangzhi"), Boss("c1_lingxuzi", "Lingxuzi"),
        Boss("c1_baw_li_guhh_lang", "Baw-Li-Guhh-Lang"), Boss("c1_guangmou", "Guangmou"), Boss("c1_whiteclad_noble", "Whiteclad Noble"),
        Boss("c1_black_wind_king", "Black Wind King"), Boss("c1_black_bear_guai", "Black Bear Guai"), Boss("c1_red_loong", "Red Loong"), Boss("c1_elder_jinchi", "Elder Jinchi"),
        // Chapter 2 — Yellow Wind Ridge
        Boss("c2_lang_li_guhh_baw", "Lang-Li-Guhh-Baw"), Boss("c2_earth_wolf", "Earth Wolf"),
        Boss("c2_king_second_prince_flowing_sands", "King and Second Prince of Flowing Sands"), Boss("c2_first_prince_flowing_sands", "First Prince of Flowing Sands"),
        Boss("c2_tiger_vanguard", "Tiger Vanguard"), Boss("c2_tigers_acolyte", "Tiger's Acolyte"), Boss("c2_stone_vanguard", "Stone Vanguard"),
        Boss("c2_gore_eye_daoist", "Gore-Eye Daoist"), Boss("c2_mother_of_stones", "Mother of Stones"), Boss("c2_man_in_stone", "Man-in-Stone"),
        Boss("c2_yellow_wind_sage", "Yellow Wind Sage"), Boss("c2_shigandang", "Shigandang"), Boss("c2_mad_tiger", "Mad Tiger"),
        Boss("c2_yellow_robed_squire", "Yellow-Robed Squire"), Boss("c2_tiger_vanguard_sahali", "Tiger Vanguard (Kingdom of Sahali)"),
        Boss("c2_fuban", "Fuban"), Boss("c2_black_loong", "Black Loong"),
        // Chapter 3 — The New West
        Boss("c3_macaque_chief_1", "Macaque Chief (First Encounter)"), Boss("c3_kang_jin_loong", "Kang-Jin Loong"),
        Boss("c3_captain_lotus_vision", "Captain Lotus-Vision"), Boss("c3_captain_wise_voice", "Captain Wise-Voice"),
        Boss("c3_macaque_chief_2", "Macaque Chief (Second Encounter)"), Boss("c3_kang_jin_star", "Kang-Jin Star"),
        Boss("c3_third_prince_flowing_sands", "Third Prince of Flowing Sands"), Boss("c3_cyan_loong", "Cyan Loong"),
        Boss("c3_apramana_bat", "Apramana Bat"), Boss("c3_chen_loong", "Chen Loong"), Boss("c3_non_white", "Non-White"),
        Boss("c3_lang_li_guhh_lang", "Lang-Li-Guhh-Lang"), Boss("c3_non_able", "Non-Able"), Boss("c3_green_capped_martialist", "Green-Capped Martialist"),
        Boss("c3_captain_void_illusion", "Captain Void-Illusion"), Boss("c3_captain_kalpa_wave", "Captain Kalpa-Wave"),
        Boss("c3_old_ginseng_guai", "Old Ginseng Guai"), Boss("c3_non_pure", "Non-Pure"), Boss("c3_non_void", "Non-Void"),
        Boss("c3_yin_tiger", "Yin Tiger"), Boss("c3_monk_from_the_sea", "Monk from the Sea"),
        Boss("c3_macaque_chief_3", "Macaque Chief (Third Encounter)"), Boss("c3_yellowbrow", "Yellowbrow"),
        // Chapter 4 — The Webbed Hollow
        Boss("c4_second_sister", "The Second Sister"), Boss("c4_elder_armourworm", "Elder Armourworm"),
        Boss("c4_venom_daoist_1", "Venom Daoist (First Encounter)"), Boss("c4_centipede_guai", "Centipede Guai"),
        Boss("c4_buddhas_right_hand", "Buddha's Right Hand"), Boss("c4_baw_li_guhh_baw", "Baw-Li-Guhh-Baw"),
        Boss("c4_zhu_bajie", "Zhu Bajie"), Boss("c4_violet_spider", "Violet Spider"), Boss("c4_commander_beetle", "Commander Beetle"),
        Boss("c4_hundred_eyed_daoist_master", "Hundred-Eyed Daoist Master"), Boss("c4_fungiwoman", "Fungiwoman"),
        Boss("c4_venom_daoist_2", "Venom Daoist (Second Encounter)"), Boss("c4_scorpionlord", "Scorpionlord"),
        Boss("c4_daoist_mi", "Daoist Mi"), Boss("c4_duskveil", "Duskveil"), Boss("c4_yellow_loong", "Yellow Loong"),
        // Chapter 5 — Flaming Mountains
        Boss("c5_pale_axe_stalwart", "Pale-Axe Stalwart"), Boss("c5_brown_iron_cart", "Brown-Iron Cart"),
        Boss("c5_gray_bronze_cart", "Gray-Bronze Cart"), Boss("c5_crimson_silver_cart", "Crimson-Silver Cart"),
        Boss("c5_rusty_gold_cart", "Rusty-Gold Cart"), Boss("c5_father_of_stones", "Father of Stones"),
        Boss("c5_fast_as_wind_quick_as_fire", "Fast as Wind and Quick as Fire"), Boss("c5_flint_chief", "Flint Chief"),
        Boss("c5_flint_vanguard", "Flint Vanguard"), Boss("c5_mother_of_flamlings", "Mother of Flamlings"),
        Boss("c5_cloudy_mist_misty_cloud", "Cloudy Mist and Misty Cloud"), Boss("c5_keeper_yin_yang_fish", "Keeper of Flaming Mountains and Yin-Yang Fish"),
        Boss("c5_nine_capped_lingzhi_guai", "Nine-Capped Lingzhi Guai"), Boss("c5_baw_lang_lang", "Baw-Lang-Lang"),
        Boss("c5_top_takes_bottom", "Top Takes Bottom and Bottom Takes Top"), Boss("c5_red_boy_yaksha_king", "Red Boy and Yaksha King"),
        Boss("c5_bishui_golden_eyed_beast", "Bishui Golden-Eyed Beast"),
        // Chapter 6 — Mount Huaguo
        Boss("c6_supreme_inspector", "Supreme Inspector"), Boss("c6_poison_chiefs", "Poison Chiefs (Four Encounters)"),
        Boss("c6_water_wood_beast", "Water-Wood Beast"), Boss("c6_son_of_stones", "Son of Stones"), Boss("c6_lang_baw_baw", "Lang-Baw-Baw"),
        Boss("c6_giant_shigandang", "Giant Shigandang"), Boss("c6_gold_armored_rhino", "Gold Armored Rhino"),
        Boss("c6_jiao_loong_of_waves", "Jiao-Loong of Waves"), Boss("c6_feng_tail_general", "Feng-Tail General"),
        Boss("c6_emerald_armed_mantis", "Emerald-Armed Mantis"), Boss("c6_stone_monkey_great_sage", "Stone Monkey and The Great Sage's Broken Shell"),
    ];

    private static BossDefinition Boss(string id, string displayName) => new(BossId.Parse(id), displayName);
}
