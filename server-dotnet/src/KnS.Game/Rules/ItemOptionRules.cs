namespace KnS.Game.Rules;

public static class ItemOptionRules
{
    /// <summary>An item has at most this many option slots.</summary>
    public const int MaxOptions = 6;

    /// <summary>The equipment ranges whose items get options.</summary>
    public static readonly IReadOnlySet<long> OptionBearingCategories =
        new HashSet<long> { 200000, 300000, 400000, 700000, 800000, 900000 };

    /// <summary>
    /// master_item_option_count's source for a stage mode ("Nomal" is the data's spelling).
    /// Armor dropped in event dungeons (stage_mode 11..15) uses the native "Event_Armor" table.
    /// </summary>
    public static string OptionCountSource(long stageMode, long category = 0) => stageMode switch
    {
        2 => "Hard",
        ShopRules.ItemShopOptionMode => "ItemShop",
        >= 11 and <= 15 when category == 300000 => "Event_Armor",
        _ => "Nomal",
    };

    /// <summary>Options of one family (option id / 100) exclude each other on an item.</summary>
    public static long FamilyOf(long optionId) => optionId / 100;
}
