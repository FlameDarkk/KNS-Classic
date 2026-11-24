using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Randomness;
using KnS.Game.Rules;
using KnS.Protocol.Json;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Shop;

/// <summary>Protocol 330: the ally gear bag, sent at login (parser case @0x15d94e). A bare reply empties it.</summary>
public sealed class FellowItemListHandler(PlayerDirectory directory, FellowItemService fellowItems) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request) =>
        ProtocolResult.Success(new FellowItemListResponse(fellowItems.GetOwned(directory.ResolveCurrent(request).UserNumber)));
}

public sealed record FellowItemListResponse(
    [property: JsonPropertyName("fellow_item_infos")] IReadOnlyList<FellowItemInfo> FellowItems);

/// <summary>
/// Protocol 338: Ally Item Draw (parser case @0x15c3f6), <c>{package_id}</c> with packages 9710401 (11 draws) and 9710402 (1 draw).
/// <c>fellow_item_infos</c> goes into the bag; <c>lottery_item_infos</c> [{id, count, fellow_item_no}], at most 11, is what the roulette stops on.
/// A bare reply leaves the roulette with nothing to show.
/// </summary>
public sealed class FellowItemDrawHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    FellowItemService fellowItemService,
    FellowItemRepository fellowItems,
    MasterDataRepository masterData,
    IRandomSource random,
    ILogger<FellowItemDrawHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long packageId = request.Parameters.GetInt64("package_id");
        ShopPackage? package = masterData.GetShopPackage(packageId);
        List<FellowItemRule> pool = masterData.GetDrawableFellowItems().Where(item => item.Grade <= ShopRules.FellowItemGradeMax).ToList();
        if (package is null || pool.Count == 0 || !profiles.TrySpend(userNumber, package.PriceItemId, package.Price))
        {
            logger.LogInformation("Ally item draw: package {Package} refused ({Row})", packageId, package);
            return ProtocolResult.Refused("cannot draw");
        }

        List<long> weights = pool.Select(item => ShopRules.FellowItemWeight(item.Grade)).ToList();
        var drawn = new List<FellowItemInfo>();
        for (long i = 0; i < package.DrawCount; i++)
        {
            long fellowItemId = random.PickWeighted(pool, weights).FellowItemId;
            long number = fellowItems.Add(userNumber, fellowItemId);
            drawn.Add(fellowItemService.ToInfo(fellowItems.Find(userNumber, number)!));
        }

        logger.LogInformation("Ally item draw: package {Package} -> [{Items}]", packageId, string.Join(",", drawn.Select(item => item.FellowItemId)));
        return ProtocolResult.Success(new FellowItemDrawResponse
        {
            Goods = profiles.GetGoods(userNumber),
            FellowItems = drawn,
            Lottery = drawn.Select(item => new LotteryItem(item.FellowItemId, 1, item.FellowItemNumber)).ToList(),
        });
    }
}

public sealed record FellowItemDrawResponse
{
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("fellow_item_infos")] public IReadOnlyList<FellowItemInfo> FellowItems { get; init; } = [];
    [JsonPropertyName("lottery_item_infos")] public IReadOnlyList<LotteryItem> Lottery { get; init; } = [];
}

public sealed record LotteryItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("count")] long Count,
    [property: JsonPropertyName("fellow_item_no")] long FellowItemNumber);

/// <summary>
/// Protocol 333: ally gear on or off (parser case @0x15ae64, post-handler @0x156cdc), <c>{fellow_no, fellow_item_no, equip_index}</c>;
/// no item takes off what the slot holds. The client replaces the ally from <c>fellow_info</c> and writes <c>fellow_no</c> on the bag rows
/// named by <c>equip_fellow_item_no</c> and <c>unequip_fellow_item_no</c>.
/// </summary>
public sealed class FellowItemEquipHandler(
    PlayerDirectory directory,
    FellowService fellowService,
    FellowRepository fellows,
    FellowItemRepository fellowItems,
    ILogger<FellowItemEquipHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long fellowNumber = request.Parameters.GetInt64("fellow_no");
        long itemNumber = request.Parameters.GetInt64("fellow_item_no");
        long equipIndex = request.Parameters.GetInt64("equip_index");

        FellowItemRecord? item = fellowItems.Find(userNumber, itemNumber);
        string? refusal = !fellows.GetOwnedFellowNumbers(userNumber).Contains(fellowNumber) ? "no such ally"
            : equipIndex is < FellowRules.WeaponEquipIndex or > FellowRules.Accessory2EquipIndex ? "no such slot"
            : itemNumber == 0 ? null
            : item is null ? "no such item"
            : item.FellowNumber != 0 && item.FellowNumber != fellowNumber ? "worn by another ally"
            : !FellowRules.FitsEquipIndex(item.FellowItemId, equipIndex) ? "wrong slot"
            : null;
        if (refusal is not null)
        {
            logger.LogInformation("Ally item equip refused: {Reason} (fellow_no {Fellow}, item {Item}, slot {Slot})", refusal, fellowNumber, itemNumber, equipIndex);
            return ProtocolResult.Refused(refusal);
        }

        FellowItemRecord? previous = fellowItems.GetOwned(userNumber)
            .FirstOrDefault(worn => worn.FellowNumber == fellowNumber && worn.EquipIndex == equipIndex && worn.FellowItemNumber != itemNumber);
        if (previous is not null)
        {
            fellowItems.SetEquipped(previous.FellowItemNumber, 0, 0);
        }

        if (item is not null)
        {
            fellowItems.SetEquipped(itemNumber, fellowNumber, equipIndex);
        }

        logger.LogInformation("Ally item equip: fellow_no {Fellow} slot {Slot}: on {On}, off {Off}", fellowNumber, equipIndex, itemNumber, previous?.FellowItemNumber ?? 0);
        return ProtocolResult.Success(new FellowItemEquipResponse(
            fellowService.GetFellowInfos(userNumber, new HashSet<long> { fellowNumber }).Single(),
            itemNumber,
            previous?.FellowItemNumber ?? 0));
    }
}

public sealed record FellowItemEquipResponse(
    [property: JsonPropertyName("fellow_info")] FellowInfo Fellow,
    [property: JsonPropertyName("equip_fellow_item_no")] long EquippedItem,
    [property: JsonPropertyName("unequip_fellow_item_no")] long UnequippedItem);

/// <summary>
/// Protocol 334: locks or unlocks ally gear (parser case @0x15dc82, post-handler @0x1566d6), <c>{fellow_item_no, fellow_item_lock}</c>.
/// The post-handler writes the lock through the bag row it looks up without a null check, so an unknown item gets a refusal, not a reply.
/// </summary>
public sealed class FellowItemLockHandler(
    PlayerDirectory directory,
    FellowItemRepository fellowItems,
    ILogger<FellowItemLockHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long itemNumber = request.Parameters.GetInt64("fellow_item_no");
        bool wanted = request.Parameters.GetInt64("fellow_item_lock") != 0;
        if (fellowItems.Find(userNumber, itemNumber) is null)
        {
            return ProtocolResult.Refused("no such item");
        }

        fellowItems.SetLocked(itemNumber, wanted);
        logger.LogInformation("Ally item lock: {Item} -> {Locked}", itemNumber, wanted);
        return ProtocolResult.Success(new FellowItemLockResponse(itemNumber, wanted ? 1 : 0));
    }
}

public sealed record FellowItemLockResponse(
    [property: JsonPropertyName("fellow_item_no")] long FellowItemNumber,
    [property: JsonPropertyName("fellow_item_lock")] long Locked);

/// <summary>
/// Protocol 337: sells ally gear (parser case @0x160afe, post-handler @0x157426), <c>{sell_fellow_item_no_list}</c>. The price is the one the
/// client shows: GetItemSellPrice@0xf5c00 reads int 40 of the master row, the same for every upgrade. Locked and worn gear is refused.
/// </summary>
public sealed class FellowItemSellHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    FellowItemRepository fellowItems,
    MasterDataRepository masterData,
    ILogger<FellowItemSellHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        var sold = new List<long>();
        var failed = new List<long>();
        long earned = 0;

        foreach (long itemNumber in request.Parameters.GetList("sell_fellow_item_no_list").Select(LenientJson.ToInt64))
        {
            FellowItemRecord? item = fellowItems.Find(userNumber, itemNumber);
            if (item is null || item.Locked != 0 || item.FellowNumber != 0)
            {
                failed.Add(itemNumber);
                continue;
            }

            fellowItems.Delete(itemNumber);
            earned += masterData.GetFellowItem(item.FellowItemId)?.SellPrice ?? 0;
            sold.Add(itemNumber);
        }

        profiles.AddCurrency(userNumber, gold: earned);
        logger.LogInformation("Ally item sell: sold [{Sold}] for {Gold} G, failed [{Failed}]", string.Join(",", sold), earned, string.Join(",", failed));
        return ProtocolResult.Success(new FellowItemSellResponse(profiles.GetGoods(userNumber), sold, failed));
    }
}

public sealed record FellowItemSellResponse(
    [property: JsonPropertyName("update_item_etc_info")] IReadOnlyList<GoodsEntry> Goods,
    [property: JsonPropertyName("success_list")] IReadOnlyList<long> Sold,
    [property: JsonPropertyName("fail_list")] IReadOnlyList<long> Failed)
{
    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
}
