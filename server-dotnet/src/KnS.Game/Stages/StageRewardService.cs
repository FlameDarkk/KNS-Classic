using System.Text.Json.Nodes;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Inventory;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Randomness;
using KnS.Game.Rules;
using KnS.Protocol.Json;

namespace KnS.Game.Stages;

/// <summary>The drops of a stage start (71) and claiming them in the stage result (72).</summary>
/// <remarks>
/// A stage start rolls the stage's reward slots once and keeps the hits (stage_roll). Every kind
/// of drop comes from that one roll: equipment and materials become offered items, Gold Boxes
/// and counted etc items become event offers, and fellows are granted when the stage is cleared.
/// </remarks>
public sealed class StageRewardService(
    StageRepository stages,
    ItemRepository items,
    PlayerRepository players,
    GoodsRepository goods,
    MasterDataRepository masterData,
    PlayerProfileService profiles,
    InventoryService inventory,
    HeroGearRules heroGear,
    ItemOptionRoller optionRoller,
    FellowService fellows,
    IRandomSource random,
    GameOptions options)
{
    /// <summary>
    /// Rolls the stage and offers its equipment and material drops (fixed slots, then the area
    /// boss's random item) plus Gold Boxes: drop_item_list. Replaces the previous start's offers.
    /// </summary>
    public DropItemList OfferDrops(long userNumber, long stageId, long stageMode, long startTime = 0, long staminaCost = 0)
    {
        items.DeleteUnclaimed(userNumber);
        stages.ClearEventOffers(userNumber);
        stages.RecordStart(userNumber, stageId, startTime, staminaCost, stageMode);
        HashSet<long> equipment = masterData.GetEquipmentItemIds();
        HashSet<long> materials = masterData.GetMaterialItemIds();
        IReadOnlyList<long> series = heroGear.GetSeries(profiles.GetAccount(userNumber).HeroPcid);

        RollStage(userNumber, stageId);
        List<long> rolled = GetRolledItems(userNumber, id => equipment.Contains(id) || materials.Contains(id));
        rolled.AddRange(RollRandomRewards(stageId, series));

        // Never offer equipment the active hero cannot equip (CheckEquipItemSubType).
        List<long> offerable = rolled
            .Select(id => heroGear.ToHeroVersion(id, series))
            .Where(id => materials.Contains(id) || series.Count == 0 || heroGear.CanEquip(id, series))
            .Take(DropRules.MaxDropsPerList)
            .ToList();

        var drops = new List<DropOffer>();
        foreach (long itemId in offerable)
        {
            long itemNumber = items.Add(userNumber, itemId, claimed: false, inventory.GetNewItemStats(itemId));
            if (equipment.Contains(itemId))
            {
                optionRoller.Roll(userNumber, itemNumber, itemId, stageMode);
            }

            drops.Add(new DropOffer(itemId, itemNumber));
        }

        StageMonsterSetup? setup = masterData.GetStageMonsterSetup(stageId);
        bool bossStage = setup is not null && setup.NormalMonsterFile.StartsWith(DropRules.BossMonsterFilePrefix, StringComparison.Ordinal);
        int fellowSlots = Math.Min(
            GetRolledItems(userNumber, id => id <= DropRules.MaxFellowItemId && masterData.FellowExists(id)).Count,
            Math.Min(DropRules.MaxDroppedFellows, DropRules.MaxDropsPerList - drops.Count));
        foreach (long fellowId in GetRolledItems(userNumber, id => id <= DropRules.MaxFellowItemId && masterData.FellowExists(id)).Take(fellowSlots))
        {
            long offerNumber = stages.AddEventOffer(userNumber, fellowId, 1);
            drops.Add(new DropOffer(fellowId, offerNumber));
        }

        StageGoldSettings? goldSettings = masterData.GetStageGoldSettings(stageId);
        long fallbackGoldBox = DropRules.GoldBoxBase + DropRules.GoldBoxPerMonsterLevel * (setup?.MonsterLevel ?? 1);

        int goldBoxes = Math.Min(
            GetRolledItems(userNumber, id => id == CurrencyItems.GoldBox).Count,
            DropRules.MaxDropsPerList - drops.Count);
        for (int i = 0; i < goldBoxes; i++)
        {
            long goldBoxAmount = goldSettings is { GoldBoxMax: > 0 }
                ? random.NextInclusive(Math.Max(1, goldSettings.GoldBoxMin), Math.Max(Math.Max(1, goldSettings.GoldBoxMin), goldSettings.GoldBoxMax))
                : fallbackGoldBox;
            long offerNumber = stages.AddEventOffer(userNumber, CurrencyItems.GoldBox, goldBoxAmount);
            // The boss list sends the box as gold, which CreateDropItem draws as the Gold Box.
            drops.Add(new DropOffer(bossStage ? CurrencyItems.Gold : CurrencyItems.GoldBox, offerNumber));
        }

        return bossStage ? new DropItemList([], drops) : new DropItemList(drops, []);
    }

    /// <summary>
    /// Offers the rolled counted etc items (19xxxxx except the Gold Box, e.g. the World Tree
    /// Branch): drop_event_item_list, at most two kinds; count = how many monsters carry one.
    /// Runs after <see cref="OfferDrops"/>, which cleared the previous offers.
    /// </summary>
    public IReadOnlyList<EventDropOffer> OfferEventItems(long userNumber)
    {
        HashSet<long> etcItems = masterData.GetEtcItemIds();
        var kinds = new List<long>();
        var counts = new Dictionary<long, long>();
        foreach (long itemId in GetRolledItems(userNumber, id => etcItems.Contains(id) && id != CurrencyItems.GoldBox))
        {
            if (!counts.TryAdd(itemId, 1))
            {
                counts[itemId]++;
            }
            else
            {
                kinds.Add(itemId);
            }
        }

        return kinds.Take(DropRules.MaxEventItemKinds)
            .Select(itemId => new EventDropOffer(itemId, counts[itemId], stages.AddEventOffer(userNumber, itemId, counts[itemId])))
            .ToList();
    }

    /// <summary>
    /// Turns the offered items the client reports as picked up into owned ones. With
    /// <paramref name="claimRest"/> (a cleared stage) every other offered item is claimed too: a drop
    /// the hero never walked over (the stage ended first) still belongs to the player.
    /// </summary>
    public List<ItemInfo> ClaimItems(long userNumber, IReadOnlyList<JsonNode?> rewardItems, bool claimRest = false)
    {
        var wanted = new SortedSet<long>();
        foreach (JsonNode? reported in rewardItems)
        {
            JsonNode? itemNumber = reported is JsonObject entry ? entry["item_no"] : reported;
            if (LenientJson.TryToInt64(itemNumber) is { } number)
            {
                wanted.Add(number);
            }
        }

        var claimed = new List<ItemInfo>();
        foreach (long itemNumber in wanted)
        {
            if (items.Claim(userNumber, itemNumber) is { } item)
            {
                claimed.Add(inventory.ToItemInfo(item));
            }
        }

        if (claimRest)
        {
            foreach (ItemRecord pending in items.GetUnclaimed(userNumber))
            {
                if (items.Claim(userNumber, pending.ItemNumber) is { } item)
                {
                    claimed.Add(inventory.ToItemInfo(item));
                }
            }
        }

        return claimed;
    }

    /// <summary>
    /// Credits the event offers. Gold Boxes come back in reward_items by offer number and credit
    /// their whole amount to the wallet, also on a failed run. On a clear every offer is credited
    /// in full whether picked up or not (a boss's burst drops can still lie on the ground when the
    /// stage ends); reported ones come first so the chest row keeps the pickup order.
    /// Fellow offers (<= MaxFellowItemId) are handled by <see cref="GrantDroppedFellows"/>.
    /// </summary>
    /// <returns>What was credited, in report order, with the credited counts.</returns>
    public IReadOnlyList<EventOfferRecord> ClaimEventOffers(
        long userNumber, IEnumerable<long> reportedItemNumbers, bool cleared = true)
    {
        Dictionary<long, EventOfferRecord> offers = stages.GetEventOffers(userNumber)
            .Where(offer => offer.ItemId > DropRules.MaxFellowItemId)
            .ToDictionary(offer => offer.ItemNumber);
        List<long> reportOrder = reportedItemNumbers.Distinct().ToList();

        var credited = new List<EventOfferRecord>();
        var totals = new List<(long ItemId, long Count)>();
        void Credit(EventOfferRecord offer, long count)
        {
            if (count <= 0)
            {
                return;
            }

            credited.Add(offer with { Count = count });
            int index = totals.FindIndex(total => total.ItemId == offer.ItemId);
            if (index >= 0)
            {
                totals[index] = (offer.ItemId, totals[index].Count + count);
            }
            else
            {
                totals.Add((offer.ItemId, count));
            }
        }

        foreach (long itemNumber in reportOrder)
        {
            if (!offers.Remove(itemNumber, out EventOfferRecord? offer))
            {
                continue;
            }

            if (offer.ItemId == CurrencyItems.GoldBox || cleared)
            {
                Credit(offer, offer.Count);
            }
        }

        // Unpicked drops of a cleared stage are still the player's.
        if (cleared)
        {
            foreach (EventOfferRecord offer in offers.Values)
            {
                Credit(offer, offer.Count);
            }
        }

        foreach ((long itemId, long count) in totals)
        {
            if (itemId == CurrencyItems.GoldBox)
            {
                players.AddCurrency(userNumber, gold: count);
            }
            else
            {
                goods.Add(userNumber, itemId, count);
            }
        }

        stages.ClearEventOffers(userNumber);
        return credited;
    }

    /// <summary>
    /// The stage result's item_infos (the chest row): the claimed items and the picked-up Gold
    /// Boxes, in the order the client reported its pickups. Gold Boxes need an entry even though
    /// their gold is already in the wallet. Counted etc items (World Tree Branch) are not chests:
    /// the clear screen draws them beside the row with the offered count (see
    /// <see cref="ClaimEventOffers"/>), so a chest for them showed the branch twice. The zeros their
    /// pickups add to reward_items open nothing.
    /// </summary>
    public static IReadOnlyList<object> OrderChests(
        IReadOnlyList<JsonNode?> rewardItems, IReadOnlyList<ItemInfo> claimedItems, IReadOnlyList<EventOfferRecord> creditedOffers,
        int droppedFellows = 0)
    {
        List<CountedItemInfo> events = creditedOffers
            .Where(offer => offer.ItemId == CurrencyItems.GoldBox)
            .Select(offer => new CountedItemInfo(offer.ItemNumber, offer.ItemId, offer.Count)).ToList();
        var remaining = claimedItems.ToList();
        var ordered = new List<object>();

        foreach (JsonNode? reported in rewardItems)
        {
            if (!TryGetWholeNumber(reported, out long itemNumber) || itemNumber == 0)
            {
                continue;
            }

            CountedItemInfo? counted = events.FirstOrDefault(item => item.ItemNumber == itemNumber);
            if (counted is not null)
            {
                ordered.Add(counted);
                events.Remove(counted);
                continue;
            }

            int index = remaining.FindIndex(item => item.ItemNumber == itemNumber);
            if (index >= 0)
            {
                ordered.Add(remaining[index]);
                remaining.RemoveAt(index);
            }
        }

        ordered.AddRange(events);
        ordered.AddRange(remaining);
        return FitChestRow(ordered, Math.Max(0, MaxResultChests - droppedFellows));
    }

    /// <summary>
    /// The result screen's chest row holds four chests: GetStageClearTouchID@0xe5ee8 and the draw
    /// at @0xe9548 lay out and hit-test slots 0-3 only (220 px apart, centered), while the 72
    /// parser keeps the full item_infos count, so a fifth chest is drawn off-screen, cannot be
    /// tapped and the screen never closes. The client opens item_infos plus drop_fellow_infos
    /// as chests (post-72 handler @0x158a00), so dropped allies use slots of the four. Everything is already credited, so surplus counted
    /// chests of one item merge into the first of their kind, then any remainder is cut.
    /// </summary>
    public const int MaxResultChests = 4;

    private static List<object> FitChestRow(List<object> chests, int limit)
    {
        if (chests.Count <= limit)
        {
            return chests;
        }

        var merged = new List<object>();
        foreach (object chest in chests)
        {
            int at = chest is CountedItemInfo counted
                ? merged.FindIndex(m => m is CountedItemInfo c && c.ItemId == counted.ItemId)
                : -1;
            if (at >= 0)
            {
                var first = (CountedItemInfo)merged[at];
                merged[at] = first with { Count = first.Count + ((CountedItemInfo)chest).Count };
            }
            else
            {
                merged.Add(chest);
            }
        }

        return merged.Take(limit).ToList();
    }

    /// <summary>
    /// What the last stage start left to pick up: the offered items, the counted offers (Gold
    /// Boxes, event items) and every rolled slot, fellows included. For the drop audit.
    /// </summary>
    /// <summary>Consumes the open stage start (see <see cref="OfferDrops"/>); false when the result has no start.</summary>
    public StageStartRecord? GetStart(long userNumber) => stages.GetStart(userNumber);

    public StageStartRecord? TakeStart(long userNumber) => stages.TakeStart(userNumber);

    public PendingDrops GetPendingDrops(long userNumber) => new(
        items.GetUnclaimed(userNumber),
        stages.GetEventOffers(userNumber),
        stages.GetRolledRewards(userNumber));

    /// <summary>
    /// Grants a cleared stage's rolled fellows: every offered fellow chest, picked up or not
    /// (falls back to the roll when nothing was offered). Returns their fellow numbers.
    /// </summary>
    public IReadOnlyList<long> GrantDroppedFellows(long userNumber)
    {
        List<EventOfferRecord> fellowOffers = stages.GetEventOffers(userNumber)
            .Where(offer => offer.ItemId <= DropRules.MaxFellowItemId && masterData.FellowExists(offer.ItemId))
            .ToList();
        IReadOnlyList<long> granted;
        if (fellowOffers.Count > 0)
        {
            granted = fellowOffers
                .Take(DropRules.MaxDroppedFellows)
                .Select(offer => fellows.Grant(userNumber, offer.ItemId))
                .ToList();
        }
        else
        {
            // Only real allies: rows of stages 3303083/3303091 are column-shifted (ids 1, 4), which would create broken allies.
            granted = GetRolledItems(userNumber, id => id <= DropRules.MaxFellowItemId && masterData.FellowExists(id))
                .Take(DropRules.MaxDroppedFellows)
                .Select(fellowId => fellows.Grant(userNumber, fellowId))
                .ToList();
        }
        stages.ClearRolledRewards(userNumber);
        return granted;
    }

    /// <summary>
    /// Rolls each slot group independently using the stage's native roll counts (dt_stage_pkg_140
    /// ints[112..115]: normal monster group 0-9 min..max rolls, boss group 10-13 min..max rolls).
    /// Each roll draws one 0-99 cumulative check (like GetDropItemID@0xf4aa0); repeated hits on
    /// the same slot accumulate in stage_roll.
    /// </summary>
    private void RollStage(long userNumber, long stageId)
    {
        stages.ClearRolledRewards(userNumber);
        Dictionary<long, StageRewardSlot> slots = masterData.GetRewardSlots(stageId).ToDictionary(slot => slot.Slot);
        if (options.DropEverything)
        {
            foreach ((long first, long last) in DropRules.RewardSlotGroups)
            {
                for (long slotNumber = first; slotNumber <= last; slotNumber++)
                {
                    if (!slots.TryGetValue(slotNumber, out StageRewardSlot? slot))
                    {
                        continue;
                    }

                    long count = random.NextInclusive(Math.Max(1, slot.MinCount), Math.Max(Math.Max(1, slot.MinCount), slot.MaxCount));
                    stages.AddRolledReward(userNumber, slot.Slot, slot.ItemId, count);
                }
            }

            return;
        }

        StageDropRollCounts rollCounts = masterData.GetStageDropRollCounts(stageId);
        (long First, long Last, long MinRolls, long MaxRolls)[] groups =
        [
            (DropRules.RewardSlotGroups[0].First, DropRules.RewardSlotGroups[0].Last, rollCounts.NormalMin, rollCounts.NormalMax),
            (DropRules.RewardSlotGroups[1].First, DropRules.RewardSlotGroups[1].Last, rollCounts.BossMin, rollCounts.BossMax),
        ];

        foreach ((long first, long last, long minRolls, long maxRolls) in groups)
        {
            if (maxRolls <= 0)
            {
                continue;
            }

            long rolls = random.NextInclusive(minRolls, maxRolls);
            for (long r = 0; r < rolls; r++)
            {
                long roll = random.NextInclusive(0, 99);
                long cumulative = 0;
                for (long slotNumber = first; slotNumber <= last; slotNumber++)
                {
                    if (!slots.TryGetValue(slotNumber, out StageRewardSlot? slot))
                    {
                        continue;
                    }

                    cumulative += slot.Chance;
                    if (roll < cumulative)
                    {
                        long count = random.NextInclusive(Math.Max(1, slot.MinCount), Math.Max(Math.Max(1, slot.MinCount), slot.MaxCount));
                        stages.AddRolledReward(userNumber, slot.Slot, slot.ItemId, count);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>The item ids the last roll hit, each repeated by its rolled count, for the ids <paramref name="wanted"/> accepts.</summary>
    private List<long> GetRolledItems(long userNumber, Func<long, bool> wanted) =>
        stages.GetRolledRewards(userNumber)
            .Where(reward => wanted(reward.ItemId))
            .SelectMany(reward => Enumerable.Repeat(reward.ItemId, (int)reward.Count))
            .ToList();

    /// <summary>
    /// Rolls an area boss's "?" reward: each hit is a uniform pick within the grade range among
    /// the hero's own upgrade line for that item type (type 2 / 3 / 4 = series200 / 300 / 400;
    /// the knights' type * 100 when the hero is unknown). That skips shop and event specials.
    /// </summary>
    private List<long> RollRandomRewards(long stageId, IReadOnlyList<long> series)
    {
        var picks = new List<long>();
        foreach (StageRandomReward reward in masterData.GetRandomRewards(stageId))
        {
            long subType = series.Count > 0 && reward.ItemType is >= 2 and <= 4
                ? series[(int)reward.ItemType - 2]
                : reward.ItemType * 100;
            IReadOnlyList<long> candidates = masterData.GetUpgradeLineItems(reward.ItemType, subType, reward.GradeMin, reward.GradeMax);
            if (candidates.Count > 0 && random.NextInclusive(1, 100) <= reward.Chance)
            {
                long minCount = Math.Max(1, reward.MinCount);
                long count = random.NextInclusive(minCount, Math.Max(minCount, reward.MaxCount));
                picks.AddRange(random.PickMany(candidates, count));
            }
        }

        return picks;
    }

    private static bool TryGetWholeNumber(JsonNode? node, out long number)
    {
        number = 0;
        if (LenientJson.IsInteger(node, out number))
        {
            return true;
        }

        // A float such as 2001.0 matched an item number on the Python server too.
        if (node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            double asDouble = double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
            if (asDouble == Math.Floor(asDouble))
            {
                number = (long)asDouble;
                return true;
            }
        }

        return false;
    }
}

/// <summary>The drops a stage start left open (see <see cref="StageRewardService.GetPendingDrops"/>).</summary>
public sealed record PendingDrops(
    IReadOnlyList<ItemRecord> Items,
    IReadOnlyList<EventOfferRecord> CountedItems,
    IReadOnlyList<RolledRewardRecord> Rolled);

/// <summary>drop_item_list: chests for random monsters ("1"), and the boss's drop ("4").</summary>
public sealed record DropItemList(
    [property: System.Text.Json.Serialization.JsonPropertyName(DropRules.ChestListKey)] IReadOnlyList<DropOffer> Chests,
    [property: System.Text.Json.Serialization.JsonPropertyName(DropRules.BossListKey)] IReadOnlyList<DropOffer> BossDrops);
