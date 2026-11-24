using KnS.Data.Repositories;
using KnS.Game.Models;

namespace KnS.Game.Fellows;

/// <summary>Builds the wire view of ally gear from its row and master data.</summary>
public sealed class FellowItemService(FellowItemRepository fellowItems, MasterDataRepository masterData)
{
    public IReadOnlyList<FellowItemInfo> GetOwned(long userNumber) =>
        fellowItems.GetOwned(userNumber).Select(ToInfo).ToList();

    public FellowItemInfo ToInfo(FellowItemRecord item)
    {
        FellowItemRule rule = masterData.GetFellowItem(item.FellowItemId) ?? new FellowItemRule();
        return new FellowItemInfo
        {
            FellowItemNumber = item.FellowItemNumber,
            FellowItemId = item.FellowItemId,
            Upgrade = item.Upgrade,
            Attack = rule.Attack + (item.Upgrade * rule.AttackUp),
            SkillAttack = rule.SkillAttack + (item.Upgrade * rule.SkillAttackUp),
            Critical = rule.Critical + (item.Upgrade * rule.CriticalUp),
            CriticalDamage = rule.CriticalDamage + (item.Upgrade * rule.CriticalDamageUp),
            AttackSpeed = rule.AttackSpeed + (item.Upgrade * rule.AttackSpeedUp),
            Locked = item.Locked,
            FellowNumber = item.FellowNumber,
            UpgradeExperience = item.Upgrade >= MaxUpgrade(rule) ? 0 : item.UpgradeExperience,
            UpgradeNeededExperience = item.Upgrade >= MaxUpgrade(rule) ? 0 : NeededExperience(rule, item.Upgrade),
        };
    }

    public static long MaxUpgrade(FellowItemRule rule) => rule.MaxUpgrade > 0 ? rule.MaxUpgrade : 5;

    /// <summary>Exp from <paramref name="upgrade"/> to the next level (GetAddExp@0x84160): int 6, grown by int 7 % per level.</summary>
    public static long NeededExperience(FellowItemRule rule, long upgrade)
    {
        long need = Math.Max(1, rule.NeedExp);
        for (long level = 0; level < upgrade; level++) need += need * rule.Growth / 100;
        return need;
    }

    /// <summary>Exp a fed item gives: int 8 plus 10 % of it per level of the fed item.</summary>
    public static long FeedExperience(FellowItemRule rule, long upgrade) => rule.FeedExp + (rule.FeedExp * upgrade * 10 / 100);
}
