using KnS.Data.Repositories;

namespace KnS.Game.Rules;

/// <summary>
/// Shop odds and sizes. The data holds no draw odds (the original server rolled them), so the weights are our own tuning:
/// lower grades are common, matching the 2~4 star label of the Pendant Draw and the 7 stars of the Ally Item Draw.
/// </summary>
public static class ShopRules
{
    /// <summary>The star grade of the Hero Point Shop's Draw 3-Star, 4-Star and 5-Star (dt_shop_pkg_140 table 8); null for the other packages.</summary>
    public static long? FixedFellowGrade(long packageId) => packageId switch
    {
        9710201 => 3,
        9710202 => 4,
        9710203 => 5,
        _ => null,
    };

    /// <summary>The premium ally shop advertises three through five stars.</summary>
    public static bool IsPremiumFellowDraw(long packageId) => packageId is 9710005 or 9710006;

    /// <summary>The friendship ally shop advertises one through three stars.</summary>
    public static bool IsFriendshipFellowDraw(long packageId) => packageId is 9710007 or 9710008;

    public const long PendantGradeMin = 2;
    public const long PendantGradeMax = 4;

    /// <summary>The weight of a pendant of grade 2, 3 and 4.</summary>
    public static long PendantWeight(long grade) => grade switch { 2 => 70, 3 => 25, _ => 5 };

    public const long FellowItemGradeMax = 7;

    /// <summary>The weight of a drawn ally item of grade 1 to 7.</summary>
    public static long FellowItemWeight(long grade) => grade switch { 1 => 30, 2 => 25, 3 => 18, 4 => 12, 5 => 8, 6 => 5, _ => 2 };

    /// <summary>The number of offers on the Black Market's list: the screen has a 3 x 3 grid.</summary>
    public const long BlackMarketSize = 9;

    /// <summary>Grades shown below each slot's top grade (window [top-2, top]).</summary>
    public const long BlackMarketGradeWindow = 2;

    /// <summary>
    /// The Black Market's offers for a player whose progress grade is <paramref name="progressGrade"/> (the best of the grades
    /// their cleared stages drop and the equipment they own; 0 with neither). Each equipment range (weapon, shield, armor) gets
    /// its own window [top-2, top], top being that range's highest grade not above the progress grade (its lowest grade when
    /// all are above), so armor, whose shop line ends at grade 6, keeps showing while weapons and shields climb to grade 13.
    /// Restoration choice; no native stock rule exists.
    /// </summary>
    public static List<BuyableItem> BlackMarketPool(IEnumerable<BuyableItem> eligible, long progressGrade)
    {
        long progress = Math.Max(1, progressGrade);
        var pool = new List<BuyableItem>();
        foreach (IGrouping<long, BuyableItem> range in eligible.GroupBy(item => EquipmentRanges.CategoryOf(item.ItemId)))
        {
            long top = range.Where(item => item.Grade <= progress).Select(item => item.Grade).DefaultIfEmpty(range.Min(item => item.Grade)).Max();
            pool.AddRange(range.Where(item => item.Grade <= top && item.Grade >= top - BlackMarketGradeWindow));
        }

        return pool;
    }

    public const long BlackMarketLifetimeSeconds = 3600;

    /// <summary>The Option count table new shop equipment rolls from (Get_Item_Option_Number "ItemShop").</summary>
    public const long ItemShopOptionMode = 3;

    /// <summary>The gems the Black Market's Refresh button costs: the client's prompt says 1.</summary>
    public const long BlackMarketRefreshGems = 1;
}
