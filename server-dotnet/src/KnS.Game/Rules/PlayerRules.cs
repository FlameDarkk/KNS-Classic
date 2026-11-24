namespace KnS.Game.Rules;

/// <summary>
/// Player values the client has no master-data cross-check for. Tune them live: the server
/// picks them up on restart.
/// </summary>
public static class PlayerRules
{
    /// <summary>The exp bar's size when master_level has no row for the level.</summary>
    public const long FallbackMaxExperience = 100;

    public const long Stamina = 100;

    /// <summary>A hunger point regrows every this many seconds (dt_etc_setting.json Store "StaminaRefreshTime").</summary>
    public const long StaminaRefillSeconds = 300;

    /// <summary>
    /// Hero Point stamina upgrades, dt_shop_pkg_140 table 10 (GetShopStaminaData@0xf93a4): row n =
    /// (n, MAX Stamina bonus 2n, its price 2500, recovery seconds saved 10n, its price 2500).
    /// Ten purchases each; the bonus of the n-th row is the total after n purchases.
    /// </summary>
    public const long StaminaUpgradeLimit = 10;

    public const long StaminaUpgradePrice = 2500;

    /// <summary>Level 1 starts at 100; each later player level adds one to the base capacity.</summary>
    public static long BaseMaxStamina(long level) => Stamina + Math.Max(0, level - 1);

    /// <summary>Level-based capacity plus Hero Point MAX Stamina upgrades (UpdateUserInfo adds row +4).</summary>
    public static long MaxStamina(long level, long purchases) => BaseMaxStamina(level) + 2 * Math.Clamp(purchases, 0, StaminaUpgradeLimit);

    /// <summary>Seconds per regrown point after <paramref name="purchases"/> Recovery Time upgrades (row +0xc).</summary>
    public static long StaminaRefillSecondsAfter(long purchases) => StaminaRefillSeconds - 10 * Math.Clamp(purchases, 0, StaminaUpgradeLimit);

    /// <summary>Free item bag slots; the client's cap is these plus the bought ones (native 50).</summary>
    public const long FreeItemSlots = 50;

    public const long HeroAttack = 10;
    public const long HeroDefence = 10;
    public const long HeroHp = 100;

    /// <summary>The hero_no of the hero every player owns.</summary>
    public const long DefaultHeroNumber = 1;

    /// <summary>The name shown for a player without a nickname: "Squire" + user number.</summary>
    public static string DefaultNickname(long userNumber) => "Squire" + userNumber;
}
