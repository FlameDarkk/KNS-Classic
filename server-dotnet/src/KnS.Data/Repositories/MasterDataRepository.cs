namespace KnS.Data.Repositories;

/// <summary>
/// Read-only lookups in the master tables (seeded from the game's own data). The tables keep the
/// exporter's column names, including the unnamed <c>cNN</c> ones; this class gives them names.
/// </summary>
public sealed class MasterDataRepository(DataSession session)
{
    /// <summary>SpecialItemValue from dt_etc_setting: XP-potion and book effects.</summary>
    public long? GetSpecialItemValue(long itemId) =>
        session.QueryFirstOrDefault<long?>("SELECT value FROM master_special_item WHERE item_id=@itemId", new { itemId });

    private const string FellowItemColumns =
        "fellow_item_id AS FellowItemId, grade AS Grade, attack AS Attack, attack_up AS AttackUp,"
        + " skill_attack AS SkillAttack, skill_attack_up AS SkillAttackUp, critical AS Critical, critical_up AS CriticalUp,"
        + " critical_damage AS CriticalDamage, critical_damage_up AS CriticalDamageUp,"
        + " attack_speed AS AttackSpeed, attack_speed_up AS AttackSpeedUp, c40 AS SellPrice,"
        + " max_upgrade AS MaxUpgrade, need_exp AS NeedExp, growth AS Growth, feed_exp AS FeedExp, c43 AS C43, c44 AS C44";

    /// <summary>
    /// master_stage column taken as the exp a cleared stage awards. Unconfirmed: it is the only
    /// per-stage field that grows like a reward (20/24/28 for stages 3000001-3).
    /// </summary>
    private const string StageExperienceColumn = "c87";

    /// <summary>The equipment ranges that have an allow_* column in master_item_option.</summary>
    private static readonly HashSet<long> OptionBands = [200000, 300000, 400000, 700000, 800000, 900000];

    // ------------------------------------------------------------------ levels

    /// <summary>Exp needed to leave a player level (master_level.exp_to_next), or null for an unknown level.</summary>
    public long? GetExperienceToNextLevel(long level) =>
        session.QueryFirstOrDefault<long?>("SELECT exp_to_next FROM master_level WHERE level=@level", new { level });

    /// <summary>Native base max stamina for a player level (master_level.max_stamina), or null for an unknown level.</summary>
    public long? GetMaxStamina(long level) =>
        session.QueryFirstOrDefault<long?>("SELECT max_stamina FROM master_level WHERE level=@level", new { level });

    /// <summary>Player level → exp needed to leave it.</summary>
    public Dictionary<long, long> GetExperienceTable() =>
        session.Query<(long Level, long ExperienceToNext)>("SELECT level, exp_to_next FROM master_level")
            .ToDictionary(row => row.Level, row => row.ExperienceToNext);

    /// <summary>Fellow level → exp needed to leave it, for one fellow grade.</summary>
    public Dictionary<long, long> GetFellowExperienceTable(long grade) =>
        session.Query<(long Level, long ExperienceToNext)>(
                "SELECT level, exp_to_next FROM master_fellow_level WHERE grade=@grade", new { grade })
            .ToDictionary(row => row.Level, row => row.ExperienceToNext);

    // ------------------------------------------------------------------ items

    public HashSet<long> GetEquipmentItemIds() => [.. session.Query<long>("SELECT item_id FROM master_item")];

    public HashSet<long> GetMaterialItemIds() => [.. session.Query<long>("SELECT item_id FROM master_material")];

    public HashSet<long> GetEtcItemIds() => [.. session.Query<long>("SELECT item_id FROM master_etc_item")];

    public bool FellowExists(long fellowId) =>
        session.Exists("SELECT 1 FROM master_fellow WHERE fellow_id=@fellowId", new { fellowId });

    public bool ItemExists(long itemId) =>
        session.Exists("SELECT 1 FROM master_item WHERE item_id=@itemId", new { itemId });

    public ItemStats? GetBaseStats(long itemId) =>
        session.QueryFirstOrDefault<ItemRecord>(
            "SELECT attack AS Attack, defence AS Defence, hp AS Hp FROM master_item WHERE item_id=@itemId", new { itemId })
            is { } row ? new ItemStats(row.Attack, row.Defence, row.Hp) : null;

    public long? GetSubType(long itemId) =>
        session.QueryFirstOrDefault<long?>("SELECT sub_type FROM master_item WHERE item_id=@itemId", new { itemId });

    /// <summary>Equipment sells for master_item.sell_price; potions, books and ingots for the price the client shows
    /// (its material row, 5th value = master_material.c4), so the Sell total and the payment agree.</summary>
    public long? GetSellPrice(long itemId) =>
        session.QueryFirstOrDefault<long?>("SELECT sell_price FROM master_item WHERE item_id=@itemId", new { itemId })
        ?? session.QueryFirstOrDefault<long?>("SELECT c4 FROM master_material WHERE item_id=@itemId", new { itemId });

    public ItemPowerUpRule? GetPowerUpRule(long itemId) =>
        session.QueryFirstOrDefault<ItemPowerUpRule>(
            "SELECT c4 AS MaxUpgrade, c5 AS BasePrice, c6 AS PricePerUpgrade,"
            + " attack_up AS AttackGain, defence_up AS DefenceGain, hp_up AS HpGain FROM master_item WHERE item_id=@itemId",
            new { itemId });

    public ItemEnhanceRule? GetEnhanceRule(long itemId) =>
        session.QueryFirstOrDefault<ItemEnhanceRule>(
            "SELECT c4 AS MaxUpgrade, next_item_id AS NextItemId, upgrade_cost AS Price,"
            + " material1 AS Material1, material2 AS Material2, material3 AS Material3,"
            + " material4 AS Material4, material5 AS Material5, material6 AS Material6"
            + " FROM master_item WHERE item_id=@itemId",
            new { itemId });

    /// <summary>
    /// Items of one type and sub_type within a grade range that are part of an upgrade line
    /// (they evolve into, or from, another item). Ordered by item id.
    /// </summary>
    public IReadOnlyList<long> GetUpgradeLineItems(long itemType, long subType, long gradeMin, long gradeMax) =>
        session.Query<long>(
            "SELECT item_id FROM master_item WHERE type=@itemType AND sub_type=@subType"
            + " AND grade BETWEEN @gradeMin AND @gradeMax AND (next_item_id <> -1"
            + " OR item_id IN (SELECT next_item_id FROM master_item)) ORDER BY item_id",
            new { itemType, subType, gradeMin, gradeMax });

    // ------------------------------------------------------------------ item options

    public ItemOptionRule? GetOptionRule(long itemId) =>
        session.QueryFirstOrDefault<ItemOptionRule>(
            "SELECT category AS Category, grade AS Grade, c31 AS HasFixedOptions,"
            + " c32 AS FixedOption1, c33 AS FixedOption2, c34 AS FixedOption3,"
            + " c35 AS FixedOption4, c36 AS FixedOption5, c37 AS FixedOption6"
            + " FROM master_item WHERE item_id=@itemId",
            new { itemId });

    /// <summary>How likely each option count is, for one item source ("Nomal", "Hard", ...).</summary>
    public IReadOnlyList<OptionCountWeight> GetOptionCountWeights(string source) =>
        session.Query<OptionCountWeight>(
            "SELECT option_count AS OptionCount, percent AS Percent FROM master_item_option_count"
            + " WHERE stage_mode_name=@source ORDER BY option_count",
            new { source });

    /// <summary>Options an item of this equipment range and grade may roll (tier 1..grade), by option id.</summary>
    public IReadOnlyList<long> GetRollableOptions(long band, long grade)
    {
        if (!OptionBands.Contains(band))
        {
            throw new ArgumentOutOfRangeException(nameof(band), band, "No option column for this equipment range.");
        }

        return session.Query<long>(
            $"SELECT option_id FROM master_item_option WHERE allow_{band}<>0 AND tier BETWEEN 1 AND @grade ORDER BY option_id",
            new { grade });
    }

    public OptionValueRange? GetOptionValueRange(long optionId) =>
        session.QueryFirstOrDefault<OptionValueRange>(
            "SELECT value1_min AS Value1Min, value1_max AS Value1Max, value2_min AS Value2Min, value2_max AS Value2Max"
            + " FROM master_item_option WHERE option_id=@optionId",
            new { optionId });

    // ------------------------------------------------------------------ heroes

    public HeroCatalogEntry? GetHero(long heroPcid) =>
        session.QueryFirstOrDefault<HeroCatalogEntry>(
            "SELECT price_gems AS PriceGems, weapon_id AS WeaponId, slot300_id AS ShieldId, armor_id AS ArmorId"
            + " FROM master_hero WHERE hero_pcid=@heroPcid",
            new { heroPcid });

    /// <summary>
    /// The item sub_types a hero can equip (series200, 300, 400, 700, 800, 900), in that order;
    /// empty for an unknown hero.
    /// </summary>
    public IReadOnlyList<long> GetHeroSeries(long heroPcid)
    {
        HeroSeriesRow? row = session.QueryFirstOrDefault<HeroSeriesRow>(
            "SELECT series200 AS Weapon, series300 AS Shield, series400 AS Armor,"
            + " series700 AS Accessory700, series800 AS Accessory800, series900 AS Accessory900"
            + " FROM master_hero WHERE hero_pcid=@heroPcid",
            new { heroPcid });
        return row is null ? [] : [row.Weapon, row.Shield, row.Armor, row.Accessory700, row.Accessory800, row.Accessory900];
    }

    private sealed record HeroSeriesRow
    {
        public long Weapon { get; init; }
        public long Shield { get; init; }
        public long Armor { get; init; }
        public long Accessory700 { get; init; }
        public long Accessory800 { get; init; }
        public long Accessory900 { get; init; }
    }

    // ------------------------------------------------------------------ fellows

    /// <summary>The power-up success table, by grade gap.</summary>
    public IReadOnlyList<FellowPowerUpRate> GetFellowPowerUpRates() =>
        session.Query<FellowPowerUpRate>(
            "SELECT grade_gap AS GradeGap, success_percent AS SuccessPercent, fail_points AS FailPoints"
            + " FROM master_fellow_power_up_rate ORDER BY grade_gap");

    /// <summary>The power-up and enhance gold of the (grade, upgrade) row of master_fellow_upgrade.</summary>
    public FellowUpgradeCosts? GetFellowUpgradeCosts(long grade, long upgrade) =>
        session.QueryFirstOrDefault<FellowUpgradeCosts>(
            "SELECT c2 AS PowerUpGold, c3 AS EnhanceGold FROM master_fellow_upgrade WHERE grade=@grade AND upgrade=@upgrade",
            new { grade, upgrade });

    /// <summary>Gold of an Ally Evolution for a grade: master_fellow_upgrade c5 (-1 = none).</summary>
    public long GetFellowEvolutionGold(long grade) =>
        session.QueryFirstOrDefault<long?>("SELECT c5 FROM master_fellow_upgrade WHERE grade=@grade AND upgrade=0", new { grade }) ?? -1;

    /// <summary>
    /// Fellows an Ally Draw can give: the native draw pool flag (int 93, MakeFellowList@0xcc574), which leaves out the 20
    /// "Limited" Arena/Altar allies, the Altar-only 105xxx results and the angels.
    /// </summary>
    public IReadOnlyList<DrawableFellow> GetDrawableFellows() =>
        GetFellowPoolFlags().Where(row => row.Drawable)
            .Select(row => new DrawableFellow { FellowId = row.FellowId, Grade = row.Grade }).ToList();

    /// <summary>Pool flags (ints 92/93/95 of raw_hex) of every master fellow with a level cap above 1.</summary>
    public IReadOnlyList<FellowPoolFlags> GetFellowPoolFlags()
    {
        var rows = session.QueryRows("SELECT fellow_id, grade, raw_hex FROM master_fellow WHERE max_level > 1 ORDER BY fellow_id");
        var flags = new List<FellowPoolFlags>(rows.Count);
        foreach (var row in rows)
        {
            string hex = Convert.ToString(row["raw_hex"], System.Globalization.CultureInfo.InvariantCulture) ?? "";
            long Int(int index) => hex.Length >= (index + 1) * 8
                ? System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(hex.AsSpan(index * 8, 8)))
                : 0;
            flags.Add(new FellowPoolFlags(Convert.ToInt64(row["fellow_id"], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToInt64(row["grade"], System.Globalization.CultureInfo.InvariantCulture), Int(92) == 1, Int(93) == 1, Int(95) != 0));
        }
        return flags;
    }

    private static readonly string[] AltarItemColumns = ["field_10", "field_14", "field_18", "field_1c", "field_20", "field_24"];
    private static readonly string[] AltarCountColumns = ["field_28", "field_2c", "field_30", "field_34", "field_38", "field_3c"];

    public AltarRecipe? GetAltarRecipe(long recipeId)
    {
        var rows = session.QueryRows("SELECT * FROM master_altar_recipe WHERE recipe_id=@recipeId", new { recipeId });
        if (rows.Count == 0) return null;
        var row = rows[0];
        long Field(string name) => Convert.ToInt64(row[name], System.Globalization.CultureInfo.InvariantCulture);
        return new AltarRecipe
        {
            RecipeId = recipeId,
            MaterialFellow1 = Field("field_04"),
            MaterialFellow2 = Field("field_08"),
            Items = AltarItemColumns.Zip(AltarCountColumns).Select(pair => (Field(pair.First), Field(pair.Second))).Where(item => item.Item1 > 0 && item.Item2 > 0).ToList(),
            CurrencyItem = Field("field_40"),
            CurrencyAmount = Field("field_44"),
            Kind = Field("field_48"),
            TransformGrade = Field("field_4c"),
            Tab = Field("field_50"),
        };
    }

    // ------------------------------------------------------------------ stages

    public long? GetStageExperience(long stageId) =>
        session.QueryFirstOrDefault<long?>(
            $"SELECT {StageExperienceColumn} FROM master_stage WHERE stage_id=@stageId", new { stageId });

    /// <summary>
    /// Native drop-roll counts from dt_stage_pkg_140 ints[112..115] (byte offsets 448..463 in raw_hex):
    /// Group 1 (normal monsters, slots 0-9) min/max rolls and Group 2 (boss, slots 10-13) min/max rolls.
    /// </summary>
    public StageDropRollCounts GetStageDropRollCounts(long stageId)
    {
        string? rawHex = session.QueryFirstOrDefault<string?>(
            "SELECT raw_hex FROM master_stage WHERE stage_id=@stageId", new { stageId });
        if (string.IsNullOrEmpty(rawHex) || rawHex.Length < 464 * 2)
        {
            return new StageDropRollCounts(1, 1, 1, 1);
        }

        byte[] bytes = Convert.FromHexString(rawHex.AsSpan(448 * 2, 16 * 2));
        int normalMin = BitConverter.ToInt32(bytes, 0);
        int normalMax = BitConverter.ToInt32(bytes, 4);
        int bossMin = BitConverter.ToInt32(bytes, 8);
        int bossMax = BitConverter.ToInt32(bytes, 12);
        return new StageDropRollCounts(
            Math.Max(0, normalMin), Math.Max(Math.Max(0, normalMin), normalMax),
            Math.Max(0, bossMin), Math.Max(Math.Max(0, bossMin), bossMax));
    }

    /// <summary>
    /// Native stage gold settings from dt_stage_pkg_140 ints[107..111] (c107..c111):
    /// loose gold pile min/max/count and Gold Box (1900015) min/max amount.
    /// </summary>
    public StageGoldSettings? GetStageGoldSettings(long stageId) =>
        session.QueryFirstOrDefault<StageGoldSettings>(
            "SELECT c107 AS PileMin, c108 AS PileMax, c109 AS PileCount, c110 AS GoldBoxMin, c111 AS GoldBoxMax"
            + " FROM master_stage WHERE stage_id=@stageId",
            new { stageId });

    public StageMonsterSetup? GetStageMonsterSetup(long stageId) =>
        session.QueryFirstOrDefault<StageMonsterSetup>(
            "SELECT monster_level AS MonsterLevel, normal_monster_file AS NormalMonsterFile, hard_monster_file AS HardMonsterFile"
            + " FROM master_stage_monster WHERE stage_id=@stageId",
            new { stageId });

    /// <summary>
    /// A monster file's row for one client monster slot at the highest level up to
    /// <paramref name="maxLevel"/>, as column name → value; null when there is none.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? GetMonsterStats(string monsterFile, long slot, long maxLevel)
    {
        var rows = session.QueryRows(
            "SELECT * FROM master_monster WHERE monster_file=@monsterFile AND slot=@slot AND level<=@maxLevel"
            + " ORDER BY level DESC LIMIT 1",
            new { monsterFile, slot, maxLevel });
        return rows.Count > 0 ? rows[0] : null;
    }

    public IReadOnlyList<StageRewardSlot> GetRewardSlots(long stageId) =>
        session.Query<StageRewardSlot>(
            "SELECT slot AS Slot, item_id AS ItemId, chance AS Chance, min_count AS MinCount, max_count AS MaxCount"
            + " FROM master_stage_reward WHERE stage_id=@stageId",
            new { stageId });

    public IReadOnlyList<StageRandomReward> GetRandomRewards(long stageId) =>
        session.Query<StageRandomReward>(
            "SELECT chance AS Chance, item_type AS ItemType, grade_min AS GradeMin, grade_max AS GradeMax,"
            + " min_count AS MinCount, max_count AS MaxCount FROM master_stage_random_reward WHERE stage_id=@stageId",
            new { stageId });

    public long? GetAreaOfStage(long stageId) =>
        session.QueryFirstOrDefault<long?>("SELECT area_id FROM master_stage_area WHERE stage_id=@stageId", new { stageId });

    public AreaRewardRule? GetAreaReward(long areaId, bool hardMode) =>
        session.QueryFirstOrDefault<AreaRewardRule>(
            hardMode
                ? "SELECT hard_clears AS ClearsNeeded, hard_reward_item_id AS RewardItemId, hard_reward_count AS RewardCount"
                  + " FROM master_stage_attainment WHERE area_id=@areaId"
                : "SELECT normal_clears AS ClearsNeeded, normal_reward_item_id AS RewardItemId, normal_reward_count AS RewardCount"
                  + " FROM master_stage_attainment WHERE area_id=@areaId",
            new { areaId });

    // ------------------------------------------------------------------ shop

    public HeroPointShopProduct? GetHeroPointShopProduct(long productId) =>
        session.QueryFirstOrDefault<HeroPointShopProduct>(
            "SELECT product_id,price_item_id,price,purchase_type,reward_item_id,reward_count FROM master_hero_point_shop WHERE product_id=@productId", new { productId });

    public FriendshipShopProduct? GetFriendshipShopProduct(long productId) =>
        session.QueryFirstOrDefault<FriendshipShopProduct>(
            "SELECT product_id AS ProductId, price_item_id AS PriceItemId, price AS Price, reward_item_id AS RewardItemId, reward_count AS RewardCount FROM master_friendship_shop WHERE product_id=@productId", new { productId });

    public GoldShopProduct? GetGoldShopProduct(long productId) =>
        session.QueryFirstOrDefault<GoldShopProduct>(
            "SELECT product_id AS ProductId, gold AS Gold, bonus_gold AS BonusGold, gem_price AS GemPrice FROM master_gold_shop WHERE product_id=@productId", new { productId });

    /// <summary>A named price from dt_etc_setting.json's Store table (e.g. "Continue").</summary>
    public Price? GetStorePrice(string name) =>
        session.QueryFirstOrDefault<Price>(
            "SELECT price_item_id AS PriceItemId, price AS Amount FROM master_store_price WHERE price_name=@name", new { name });

    /// <summary>The equipment the Black Market can offer: anything with a buy price.</summary>
    /// <summary>Items with a buy price; <see cref="BuyableItem.Special"/> marks shop/event specials (c31 = 1, e.g. 200131 Scorpion Sword).</summary>
    public IReadOnlyList<BuyableItem> GetBuyableItems() =>
        session.Query<BuyableItem>(
            "SELECT item_id AS ItemId, grade AS Grade, buy_price AS BuyPrice, CASE WHEN c31=1 THEN 1 ELSE 0 END AS Special"
            + " FROM master_item WHERE buy_price > 0");

    /// <summary>
    /// The highest equipment grade the stages a player has cleared can drop (fixed reward slots and area-boss random rolls);
    /// 0 with no clears.
    /// </summary>
    public long GetClearedDropGradeMax(long userNumber)
    {
        long fixedSlots = session.QueryFirstOrDefault<long?>(
            "SELECT MAX(i.grade) FROM stage_clear c JOIN master_stage_reward r ON r.stage_id=c.stage_id"
            + " JOIN master_item i ON i.item_id=r.item_id WHERE c.user_no=@userNumber", new { userNumber }) ?? 0;
        long randomRolls = session.QueryFirstOrDefault<long?>(
            "SELECT MAX(r.grade_max) FROM stage_clear c JOIN master_stage_random_reward r ON r.stage_id=c.stage_id"
            + " WHERE c.user_no=@userNumber", new { userNumber }) ?? 0;
        return Math.Max(fixedSlots, randomRolls);
    }

    /// <summary>The highest grade of the weapons, shields and armor a player owns, fixed-option specials left out; 0 with none.</summary>
    public long GetOwnedEquipmentGradeMax(long userNumber) =>
        session.QueryFirstOrDefault<long?>(
            "SELECT MAX(m.grade) FROM item i JOIN master_item m ON m.item_id=i.item_id"
            + " WHERE i.user_no=@userNumber AND i.claimed=1 AND m.category IN (200000, 300000, 400000) AND COALESCE(m.c31, 0)<>1",
            new { userNumber }) ?? 0;

    public long? GetBuyPrice(long itemId) =>
        session.QueryFirstOrDefault<long?>("SELECT buy_price FROM master_item WHERE item_id=@itemId", new { itemId });

    /// <summary>The pendants (category 700000) of a grade range: the Pendant Draw's pool.</summary>
    public IReadOnlyList<BuyableItem> GetPendants(long gradeMin, long gradeMax) =>
        session.Query<BuyableItem>(
            "SELECT item_id AS ItemId, grade AS Grade, buy_price AS BuyPrice FROM master_item"
            + " WHERE category=700000 AND grade BETWEEN @gradeMin AND @gradeMax",
            new { gradeMin, gradeMax });

    public IReadOnlyList<FellowItemRule> GetDrawableFellowItems() =>
        session.Query<FellowItemRule>($"SELECT {FellowItemColumns} FROM master_fellow_item WHERE drawable=1");

    public FellowItemRule? GetFellowItem(long fellowItemId) =>
        session.QueryFirstOrDefault<FellowItemRule>(
            $"SELECT {FellowItemColumns} FROM master_fellow_item WHERE fellow_item_id=@fellowItemId", new { fellowItemId });

    public FellowItemRecipe? GetFellowItemRecipe(long resultId)
    {
        var rows = session.QueryRows("SELECT * FROM master_fellow_item_recipe WHERE result_id=@resultId", new { resultId });
        if (rows.Count == 0) return null;
        var row = rows[0];
        long Field(string name) => Convert.ToInt64(row[name], System.Globalization.CultureInfo.InvariantCulture);
        return new FellowItemRecipe
        {
            ResultId = resultId,
            // main_value{i}: copies of main{i} one craft consumes (2-3 on most rows); the client sends each copy in its own
            // main_material_0x slot.
            MainMaterials = Enumerable.Range(1, 3).Where(i => Field($"main{i}") > 0)
                .SelectMany(i => Enumerable.Repeat(Field($"main{i}"), (int)Math.Clamp(Field($"main_value{i}"), 1, 3))).ToList(),
            SubMaterials = Enumerable.Range(1, 6).Select(i => (Field($"sub{i}"), Field($"sub_count{i}")))
                .Where(sub => sub.Item1 > 0 && sub.Item2 > 0).ToList(),
            CurrencyId = Field("currency_id"),
            CurrencyAmount = Math.Max(0, Field("currency_amount")),
        };
    }

    /// <summary>The hunger a stage costs (stage row int 86: 3 on the first stages).</summary>
    public long GetStageStaminaCost(long stageId) =>
        session.QueryFirstOrDefault<long?>("SELECT c86 FROM master_stage WHERE stage_id=@stageId", new { stageId }) ?? 0;

    public IReadOnlyList<long> GetDailyQuestIds() =>
        session.Query<long>("SELECT quest_id FROM master_daily_quest ORDER BY quest_id");

    public ShopPackage? GetShopPackage(long packageId) =>
        session.QueryFirstOrDefault<ShopPackage>(
            "SELECT price_item_id AS PriceItemId, price AS Price, draw_count AS DrawCount FROM master_shop_package"
            + " WHERE package_id=@packageId",
            new { packageId });

    public BagExpansion? GetBagExpansion(BagKind bag, long expansionLevel)
    {
        string table = bag == BagKind.Items ? "master_inventory_expansion" : "master_fellow_inventory_expansion";
        return session.QueryFirstOrDefault<BagExpansion>(
            $"SELECT price_item_id AS PriceItemId, price AS Price, add_space AS AddedSlots, total_space AS TotalSlots FROM {table}"
            + " WHERE expansion_level=@expansionLevel",
            new { expansionLevel });
    }
}

public sealed record StageDropRollCounts(long NormalMin, long NormalMax, long BossMin, long BossMax);

public sealed record StageGoldSettings
{
    public long PileMin { get; init; }
    public long PileMax { get; init; }
    public long PileCount { get; init; }
    public long GoldBoxMin { get; init; }
    public long GoldBoxMax { get; init; }
}
