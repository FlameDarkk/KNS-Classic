namespace KnS.Data.Repositories;

/// <summary>Power-up rule of an item (master_item; dt_item_pkg_140).</summary>
public sealed record ItemPowerUpRule
{
    /// <summary>Highest upgrade level (c4); Enhance is allowed from this level on.</summary>
    public long MaxUpgrade { get; init; }

    /// <summary>Price at upgrade 0 (c5).</summary>
    public long BasePrice { get; init; }

    /// <summary>Price added per upgrade level already reached (c6).</summary>
    public long PricePerUpgrade { get; init; }

    public long AttackGain { get; init; }
    public long DefenceGain { get; init; }
    public long HpGain { get; init; }
}

/// <summary>Enhance (evolve) rule of an item (master_item).</summary>
public sealed record ItemEnhanceRule
{
    /// <summary>The upgrade level the item must reach first (c4).</summary>
    public long MaxUpgrade { get; init; }

    public long NextItemId { get; init; }
    public long Price { get; init; }
    public long Material1 { get; init; }
    public long Material2 { get; init; }
    public long Material3 { get; init; }
    public long Material4 { get; init; }
    public long Material5 { get; init; }
    public long Material6 { get; init; }

    /// <summary>The material item ids the enhance consumes (unused material slots left out).</summary>
    public IEnumerable<long> Materials =>
        new[] { Material1, Material2, Material3, Material4, Material5, Material6 }.Where(id => id > 0);
}

/// <summary>What decides the options a new item gets (master_item).</summary>
public sealed record ItemOptionRule
{
    /// <summary>The equipment range (200000 weapon, ...): which allow_* column of master_item_option applies.</summary>
    public long Category { get; init; }

    public long Grade { get; init; }

    /// <summary>1 when the item has fixed options (c31) instead of rolled ones.</summary>
    public long HasFixedOptions { get; init; }

    public long FixedOption1 { get; init; }
    public long FixedOption2 { get; init; }
    public long FixedOption3 { get; init; }
    public long FixedOption4 { get; init; }
    public long FixedOption5 { get; init; }
    public long FixedOption6 { get; init; }

    public IEnumerable<long> FixedOptions =>
        new[] { FixedOption1, FixedOption2, FixedOption3, FixedOption4, FixedOption5, FixedOption6 }.Where(id => id > 0);
}

public sealed record OptionCountWeight
{
    public long OptionCount { get; init; }
    public long Percent { get; init; }
}

public sealed record OptionValueRange
{
    public long Value1Min { get; init; }
    public long Value1Max { get; init; }
    public long Value2Min { get; init; }
    public long Value2Max { get; init; }
}

/// <summary>One row of the power-up table by grade gap (target grade - material grade).</summary>
public sealed record FellowPowerUpRate
{
    public long GradeGap { get; init; }
    public long SuccessPercent { get; init; }

    /// <summary>The Power-up Points a failure adds.</summary>
    public long FailPoints { get; init; }
}

/// <summary>The gold a power-up (c2) and an enhance (c3) of a fellow row cost.</summary>
public sealed record FellowUpgradeCosts
{
    public long PowerUpGold { get; init; }
    public long EnhanceGold { get; init; }
}

public sealed record DrawableFellow
{
    public long FellowId { get; init; }
    public long Grade { get; init; }
}

/// <summary>
/// Pool flags of a master fellow row (dt_fellow_pkg table 0, ints 92-95): 92 = "Limited" badge (DrawFellowDetailInfo@0x135904,
/// row+0x170), 93 = in the Ally Draw preview pool (MakeFellowList@0xcc574, row+0x174), 95 = Altar transform result pool
/// (Altar transform preview, row+0x17c).
/// </summary>
public sealed record FellowPoolFlags(long FellowId, long Grade, bool Limited, bool Drawable, bool Transformable);

/// <summary>
/// One Altar recipe (dt_fellow_pkg table 1, master_altar_recipe). Kind 0 = random transform of one ally of
/// <see cref="TransformGrade"/>; kinds 1/2 = fixed result (the recipe id) from two MAX +5 copies of the material ids.
/// Tab: 0 Superb Evolution, 1 Transform, 2 Arena.
/// </summary>
public sealed record AltarRecipe
{
    public long RecipeId { get; init; }
    public long MaterialFellow1 { get; init; }
    public long MaterialFellow2 { get; init; }
    public IReadOnlyList<(long ItemId, long Count)> Items { get; init; } = [];
    public long CurrencyItem { get; init; }
    public long CurrencyAmount { get; init; }
    public long Kind { get; init; }
    public long TransformGrade { get; init; }
    public long Tab { get; init; }
}

public sealed record StageMonsterSetup
{
    public long MonsterLevel { get; init; }
    public string NormalMonsterFile { get; init; } = "";
    public string HardMonsterFile { get; init; } = "";
}

public sealed record StageRewardSlot
{
    public long Slot { get; init; }
    public long ItemId { get; init; }

    /// <summary>Percent chance, cumulative within the slot's group.</summary>
    public long Chance { get; init; }

    public long MinCount { get; init; }
    public long MaxCount { get; init; }
}

/// <summary>An area boss's "?" reward (master_stage_random_reward).</summary>
public sealed record StageRandomReward
{
    public long Chance { get; init; }
    public long ItemType { get; init; }
    public long GradeMin { get; init; }
    public long GradeMax { get; init; }
    public long MinCount { get; init; }
    public long MaxCount { get; init; }
}

public sealed record AreaRewardRule
{
    /// <summary>Clears of the area needed before the reward can be claimed.</summary>
    public long ClearsNeeded { get; init; }

    public long RewardItemId { get; init; }
    public long RewardCount { get; init; }
}

/// <summary>A price in some currency item (gold 1900008 or gems 1900009).</summary>
public sealed record Price
{
    public long PriceItemId { get; init; }
    public long Amount { get; init; }
}

/// <summary>An equipment item a shop can offer.</summary>
public sealed record BuyableItem
{
    public long ItemId { get; init; }
    public long Grade { get; init; }
    public long BuyPrice { get; init; }
    public long Special { get; init; }
}

/// <summary>An ally weapon or accessory (dt_fellowitem_pkg_140): base stats and the gain per upgrade.</summary>
public sealed record FellowItemRule
{
    public long FellowItemId { get; init; }
    public long Grade { get; init; }
    public long Attack { get; init; }
    public long AttackUp { get; init; }
    public long SkillAttack { get; init; }
    public long SkillAttackUp { get; init; }
    public long Critical { get; init; }
    public long CriticalUp { get; init; }
    public long CriticalDamage { get; init; }
    public long CriticalDamageUp { get; init; }
    public long AttackSpeed { get; init; }
    public long AttackSpeedUp { get; init; }
    public long SellPrice { get; init; }

    /// <summary>Steel Anvil power-up (dt_fellowitem_pkg ints 5-8): max level, exp for level 1, % the need grows per level,
    /// exp this item gives when fed.</summary>
    public long MaxUpgrade { get; init; }
    public long NeedExp { get; init; }
    public long Growth { get; init; }
    public long FeedExp { get; init; }

    /// <summary>Ints 43/44, unread by the client: used as the disassembly return % range (restoration choice).</summary>
    public long C43 { get; init; }
    public long C44 { get; init; }
}

public sealed record ShopPackage
{
    public long PriceItemId { get; init; }
    public long Price { get; init; }
    public long DrawCount { get; init; }
}

/// <summary>
/// A Steel Anvil craft recipe (dt_fellowitem_pkg table 2): the result, up to three main material item ids (consumed gear),
/// up to six sub-materials (1902001-1902008) with counts, and the currency cost.
/// </summary>
public sealed record FellowItemRecipe
{
    public long ResultId { get; init; }
    public IReadOnlyList<long> MainMaterials { get; init; } = [];
    public IReadOnlyList<(long ItemId, long Count)> SubMaterials { get; init; } = [];
    public long CurrencyId { get; init; }
    public long CurrencyAmount { get; init; }
}

public sealed record BagExpansion
{
    public long PriceItemId { get; init; }
    public long Price { get; init; }
    public long AddedSlots { get; init; }

    /// <summary>The bought slots after this expansion.</summary>
    public long TotalSlots { get; init; }
}

/// <summary>A hero offered on the Characters screen and the gear it comes with.</summary>
public sealed record HeroCatalogEntry
{
    public long PriceGems { get; init; }
    public long WeaponId { get; init; }
    public long ShieldId { get; init; }
    public long ArmorId { get; init; }

    public IEnumerable<long> DefaultGear => [WeaponId, ShieldId, ArmorId];
}

/// <summary>A gold bundle and its gem price from the browser shop table.</summary>
public sealed record GoldShopProduct
{
    public long ProductId { get; init; }
    public long Gold { get; init; }
    public long BonusGold { get; init; }
    public long GemPrice { get; init; }
}

/// <summary>A product from the browser friendship shop table.</summary>
public sealed record FriendshipShopProduct
{
    public long ProductId { get; init; }
    public long PriceItemId { get; init; }
    public long Price { get; init; }
    public long RewardItemId { get; init; }
    public long RewardCount { get; init; }
}

/// <summary>A product exported from active browser shop table 8.</summary>
public sealed record HeroPointShopProduct
{
    public long ProductId { get; init; }
    public long PriceItemId { get; init; }
    public long Price { get; init; }
    public long PurchaseType { get; init; }
    public long RewardItemId { get; init; }
    public long RewardCount { get; init; }
}
