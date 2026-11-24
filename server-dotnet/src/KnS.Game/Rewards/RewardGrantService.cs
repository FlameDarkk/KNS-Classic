using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Inventory;
using KnS.Game.Players;
using KnS.Game.Protocols.Arena;
using KnS.Game.Rules;

namespace KnS.Game.Rewards;

/// <summary>What one item id is, for paying it as a reward.</summary>
public enum RewardKind
{
    Unknown,
    Gold,
    Gems,
    Stamina,
    GoldBox,
    Ally,
    AllyGear,
    Item,
    BattleSpirit,
    Counted,
}

/// <summary>What a grant created: the new owned rows a reply has to list (fellow_info, item_info, fellow item lists).</summary>
public sealed class GrantedRewards
{
    public List<long> FellowNumbers { get; } = [];
    public List<long> ItemNumbers { get; } = [];
    public List<long> FellowItemNumbers { get; } = [];
}

/// <summary>
/// The one place that pays a data-driven reward. Each kind has its own owner: allies and ally gear are owned rows, equipment
/// and materials are inventory items, gold/gems are account columns, stamina is the refilling bar, and only the remaining
/// 19xxxxx counted items go to user_goods (an achievement once paid an ally into user_goods, where nobody could see it).
/// Unknown ids throw, so a bad data row fails loudly instead of losing the reward.
/// </summary>
public sealed class RewardGrantService(
    PlayerProfileService profiles,
    PlayerRepository players,
    GoodsRepository goods,
    FellowService fellows,
    FellowItemRepository fellowItems,
    ItemRepository items,
    InventoryService inventory,
    MasterDataRepository master,
    ArenaService arena)
{
    /// <summary>A single reward never creates more owned rows than this.</summary>
    public const long MaxOwnedPerGrant = 1000;

    public RewardKind Classify(long itemId) => itemId switch
    {
        CurrencyItems.Gold => RewardKind.Gold,
        CurrencyItems.Gems => RewardKind.Gems,
        CurrencyItems.Stamina => RewardKind.Stamina,
        CurrencyItems.GoldBox => RewardKind.GoldBox,
        ArenaService.BattleSpiritItem => RewardKind.BattleSpirit,
        > 0 and <= DropRules.MaxFellowItemId => master.FellowExists(itemId) ? RewardKind.Ally : RewardKind.Unknown,
        >= 1200000 and < 1400000 => RewardKind.AllyGear,
        _ when GoodsRepository.IsCountedItem(itemId) => RewardKind.Counted,
        _ when master.ItemExists(itemId) || master.GetMaterialItemIds().Contains(itemId) => RewardKind.Item,
        _ => RewardKind.Unknown,
    };

    /// <summary>Pays <paramref name="count"/> of <paramref name="itemId"/>; new owned rows are added to <paramref name="into"/>.</summary>
    public void Grant(long userNumber, long itemId, long count, GrantedRewards into)
    {
        if (count <= 0)
        {
            return;
        }

        RewardKind kind = Classify(itemId);
        if (kind is RewardKind.Ally or RewardKind.AllyGear or RewardKind.Item && count > MaxOwnedPerGrant)
        {
            throw new InvalidOperationException($"reward {itemId} x{count} exceeds {MaxOwnedPerGrant} owned rows");
        }

        switch (kind)
        {
            case RewardKind.Gold:
            case RewardKind.GoldBox:
                profiles.AddCurrency(userNumber, gold: count);
                break;
            case RewardKind.Gems:
                profiles.AddCurrency(userNumber, gems: count);
                break;
            case RewardKind.Stamina:
                (long stamina, long lastGain) = profiles.GetStamina(userNumber);
                players.SetStamina(userNumber, checked(stamina + count), lastGain);
                break;
            case RewardKind.Ally:
                for (long i = 0; i < count; i++) into.FellowNumbers.Add(fellows.Grant(userNumber, itemId));
                break;
            case RewardKind.AllyGear:
                for (long i = 0; i < count; i++) into.FellowItemNumbers.Add(fellowItems.Add(userNumber, itemId));
                break;
            case RewardKind.Item:
                for (long i = 0; i < count; i++)
                    into.ItemNumbers.Add(items.Add(userNumber, itemId, claimed: true, inventory.GetNewItemStats(itemId)));
                break;
            case RewardKind.BattleSpirit:
                arena.EnsureSpirit(userNumber);
                goods.Add(userNumber, itemId, count);
                break;
            case RewardKind.Counted:
                goods.Add(userNumber, itemId, count);
                break;
            default:
                throw new InvalidOperationException($"reward item {itemId} is not a known ally, item or currency");
        }
    }

    public GrantedRewards Grant(long userNumber, long itemId, long count)
    {
        var granted = new GrantedRewards();
        Grant(userNumber, itemId, count, granted);
        return granted;
    }
}
