namespace KnS.Data.Schema;

/// <summary>A read-only table loaded from a seed CSV exported from the game's data.</summary>
/// <param name="Table">Table name.</param>
/// <param name="SeedFile">CSV file name in the master-data folder; its header row names the columns.</param>
/// <param name="KeyColumns">Primary key columns; null means the first CSV column.</param>
/// <param name="ReplaceStoredRows">True for tables whose seed values were changed after release (restoration choices such as
/// raised rewards): seed rows then replace stored rows with the same key at startup. Otherwise startup only adds new keys.</param>
public sealed record MasterTable(string Table, string SeedFile, IReadOnlyList<string>? KeyColumns = null, bool ReplaceStoredRows = false);

public static class MasterTables
{
    public static readonly IReadOnlyList<MasterTable> All =
    [
        new("master_item", "master_items.csv", ReplaceStoredRows: true),
        new("master_hero", "master_heroes.csv"),
        new("master_etc_item", "master_etc_items.csv"),
        new("master_material", "master_materials.csv"),
        new("master_special_item", "master_special_items.csv"),
        new("master_level", "master_levels.csv"),
        new("master_stage", "master_stages.csv"),
        new("master_stage_area", "master_stage_areas.csv"),
        new("master_inventory_expansion", "master_inventory_expansions.csv", ReplaceStoredRows: true),
        new("master_monster", "master_monsters.csv", ["monster_file", "slot", "level"]),
        new("master_stage_monster", "master_stage_monsters.csv"),
        new("master_event_schedule", "master_event_schedules.csv"),
        new("master_fellow", "master_fellows.csv"),
        new("master_altar_recipe", "master_altar_recipes.csv"),
        new("master_fellow_item", "master_fellow_items.csv"),
        new("master_fellow_item_recipe", "master_fellow_item_recipes.csv"),
        new("master_fellow_mission", "master_fellow_missions.csv"),
        new("master_fellow_skill", "master_fellow_skills.csv"),
        new("master_daily_quest", "master_daily_quests.csv", ReplaceStoredRows: true),
        new("master_achievement", "master_achievements.csv", ReplaceStoredRows: true),
        new("master_story_quest", "master_story_quests.csv"),
        new("master_raid_challenge", "master_raid_challenges.csv", ReplaceStoredRows: true),
        new("master_raid_rank_reward", "master_raid_rank_rewards.csv", ReplaceStoredRows: true),
        new("master_login_reward", "master_login_rewards.csv", ["kind", "day"]),
        new("master_hero_point_shop", "master_hero_point_shop.csv"),
        new("master_friendship_shop", "master_friendship_shop.csv"),
        new("master_gold_shop", "master_gold_shop.csv"),
        new("master_shop_package", "master_shop_packages.csv"),
        new("master_store_price", "master_store_prices.csv"),
        new("master_fellow_upgrade", "master_fellow_upgrades.csv", ["grade", "upgrade"]),
        new("master_fellow_level", "master_fellow_levels.csv", ["level", "grade"]),
        new("master_fellow_power_up_rate", "master_fellow_power_up_rates.csv"),
        new("master_stage_reward", "master_stage_rewards.csv", ["stage_id", "slot"]),
        new("master_stage_random_reward", "master_stage_random_rewards.csv"),
        new("master_fellow_inventory_expansion", "master_fellow_inventory_expansions.csv", ReplaceStoredRows: true),
        new("master_stage_attainment", "master_stage_attainments.csv", ReplaceStoredRows: true),
        new("master_item_option", "master_item_options.csv"),
        new("master_item_option_count", "master_item_option_counts.csv", ["stage_mode_name", "option_count"]),
    ];
}
