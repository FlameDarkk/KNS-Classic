using KnS.Data.Repositories;
using KnS.Game.Models;
using KnS.Game.Rules;

namespace KnS.Game.Inventory;

/// <summary>A player's heroes, what they wear, and the item views the replies carry.</summary>
public sealed class InventoryService(HeroRepository heroes, ItemRepository items, MasterDataRepository masterData)
{
    /// <summary>The heroes a player owns, by hero number; every player owns the default hero.</summary>
    public IReadOnlyList<OwnedHeroRecord> GetHeroes(long userNumber)
    {
        heroes.EnsureOwned(userNumber, PlayerRules.DefaultHeroNumber, Data.StoredPlayerDefaults.HeroPcid);
        return heroes.GetOwned(userNumber);
    }

    /// <summary>
    /// What a hero wears, as (category, item number) in order: the default hero starts in the
    /// starter gear, bought heroes get their gear when bought; hero_equip rows override.
    /// </summary>
    public IReadOnlyList<EquippedItemRecord> GetEquipment(long userNumber, long heroNumber)
    {
        var worn = new List<EquippedItemRecord>();
        if (heroNumber == PlayerRules.DefaultHeroNumber)
        {
            worn.AddRange(StarterGear.Items.Select(item => new EquippedItemRecord { Category = item.Category, ItemNumber = item.ItemNumber }));
        }

        foreach (EquippedItemRecord equipped in heroes.GetEquipment(userNumber, heroNumber))
        {
            int index = worn.FindIndex(slot => slot.Category == equipped.Category);
            if (index >= 0)
            {
                worn[index] = equipped;
            }
            else
            {
                worn.Add(equipped);
            }
        }

        return worn;
    }

    /// <summary>
    /// The item id of the armor worn by the player's active hero (heroPcid), or 0 when none is worn. Friend and rank rows
    /// carry it as user_hero_item_id, which the client draws as the character's head.
    /// </summary>
    public long GetActiveArmorItemId(long userNumber, long heroPcid)
    {
        IReadOnlyList<OwnedHeroRecord> owned = GetHeroes(userNumber);
        OwnedHeroRecord? hero = owned.FirstOrDefault(h => h.HeroPcid == heroPcid) ?? (owned.Count > 0 ? owned[0] : null);
        if (hero is null)
        {
            return 0;
        }

        foreach (EquippedItemRecord equipped in GetEquipment(userNumber, hero.HeroNumber))
        {
            long itemId = StarterGear.FindByItemNumber(equipped.ItemNumber)?.ItemId
                ?? items.FindOwned(userNumber, equipped.ItemNumber)?.ItemId ?? 0;
            if (itemId != 0 && EquipmentRanges.CategoryOf(itemId) == EquipmentRanges.ArmorItems)
            {
                return itemId;
            }
        }

        return 0;
    }

    /// <summary>Item number → the hero wearing it, over all of the player's heroes.</summary>
    public Dictionary<long, long> GetWornItems(long userNumber)
    {
        var wornBy = new Dictionary<long, long>();
        foreach (OwnedHeroRecord hero in GetHeroes(userNumber))
        {
            foreach (EquippedItemRecord equipped in GetEquipment(userNumber, hero.HeroNumber))
            {
                wornBy[equipped.ItemNumber] = hero.HeroNumber;
            }
        }

        return wornBy;
    }

    /// <summary>The starter items the player still has.</summary>
    public IReadOnlyList<StarterItemInfo> GetStarterItems(long userNumber, IReadOnlyDictionary<long, long> wornBy)
    {
        HashSet<long> givenUp = items.GetGivenUpStarterItems(userNumber);
        HashSet<long> locked = items.GetLockedStarterItems(userNumber);
        return StarterGear.Items
            .Where(item => !givenUp.Contains(item.ItemNumber))
            .Select(item => new StarterItemInfo(item.ItemNumber, item.ItemId, wornBy.GetValueOrDefault(item.ItemNumber), locked.Contains(item.ItemNumber) ? 1 : 0))
            .ToList();
    }

    public HashSet<long> GetGivenUpStarterItems(long userNumber) => items.GetGivenUpStarterItems(userNumber);

    /// <summary>Every owned item (not the starter gear), oldest first.</summary>
    public IReadOnlyList<ItemInfo> GetOwnedItems(long userNumber, IReadOnlyDictionary<long, long> wornBy) =>
        items.GetOwned(userNumber)
            .Select(item => ToItemInfo(item, wornBy.GetValueOrDefault(item.ItemNumber)))
            .ToList();

    /// <summary>A hero's hero_item_infos. Inventory replies must carry the same item numbers.</summary>
    public IReadOnlyList<object> GetEquippedItemInfos(long userNumber, long heroNumber)
    {
        var infos = new List<object>();
        foreach (EquippedItemRecord equipped in GetEquipment(userNumber, heroNumber))
        {
            if (StarterGear.FindByItemNumber(equipped.ItemNumber) is { } starter)
            {
                infos.Add(new StarterItemInfo(starter.ItemNumber, starter.ItemId, heroNumber, items.GetLockedStarterItems(userNumber).Contains(starter.ItemNumber) ? 1 : 0));
            }
            else if (items.FindOwned(userNumber, equipped.ItemNumber) is { } owned)
            {
                infos.Add(ToItemInfo(owned, heroNumber));
            }
        }

        return infos;
    }

    public HeroInfo BuildHeroInfo(long userNumber, long heroNumber, long heroPcid) => new()
    {
        UserNumber = userNumber,
        HeroNumber = heroNumber,
        HeroPcid = heroPcid,
        EquippedItems = GetEquippedItemInfos(userNumber, heroNumber),
    };

    public ItemInfo ToItemInfo(ItemRecord item, long wornByHero = 0)
    {
        List<ItemOptionInfo> options = items.GetOptions(item.ItemNumber)
            .Select(option => new ItemOptionInfo(option.OptionId, option.Value1, option.Value2))
            .ToList();
        return new ItemInfo
        {
            ItemNumber = item.ItemNumber,
            ItemId = item.ItemId,
            HeroNumber = wornByHero,
            Upgrade = item.Upgrade,
            Attack = item.Attack,
            Defence = item.Defence,
            Hp = item.Hp,
            Locked = item.Locked ? 1 : 0,
            OptionCount = options.Count,
            Options = options,
        };
    }

    /// <summary>
    /// The stats of a new item: master_item's base values (never negative). The client's item
    /// popups print the master base, so new items match them.
    /// </summary>
    public ItemStats GetNewItemStats(long itemId) =>
        masterData.GetBaseStats(itemId) is { } stats
            ? new ItemStats(Math.Max(0, stats.Attack), Math.Max(0, stats.Defence), Math.Max(0, stats.Hp))
            : default;
}
