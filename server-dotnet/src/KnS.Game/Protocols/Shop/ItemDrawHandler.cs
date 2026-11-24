using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Gateway;
using KnS.Game.Inventory;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Randomness;
using KnS.Game.Rules;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Shop;

/// <summary>
/// Protocol 93: Pendant Draw (parser case @0x15ef38). Request as 92: <c>{package_id, use_ticket, group_num, achievement_list}</c>, packages
/// 9710105 (11 draws) and 9710106 (1 draw). The parser inserts every <c>item_infos</c> entry into the bag; DrawItemGambleResult then
/// reads the first one, so a bare reply crashes the game on the phone.
/// </summary>
public sealed class ItemDrawHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    InventoryService inventory,
    ItemRepository items,
    ItemOptionRoller optionRoller,
    MasterDataRepository masterData,
    IRandomSource random,
    ILogger<ItemDrawHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long packageId = request.Parameters.GetInt64("package_id");
        ShopPackage? package = masterData.GetShopPackage(packageId);
        IReadOnlyList<BuyableItem> pool = masterData.GetPendants(ShopRules.PendantGradeMin, ShopRules.PendantGradeMax);
        if (package is null || pool.Count == 0 || !profiles.TrySpend(userNumber, package.PriceItemId, package.Price))
        {
            logger.LogInformation("Pendant draw: package {Package} refused ({Row})", packageId, package);
            return ProtocolResult.Refused("cannot draw");
        }

        List<long> weights = pool.Select(item => ShopRules.PendantWeight(item.Grade)).ToList();
        var drawn = new List<ItemInfo>();
        for (long i = 0; i < package.DrawCount; i++)
        {
            long itemId = random.PickWeighted(pool, weights).ItemId;
            long itemNumber = items.Add(userNumber, itemId, claimed: true, inventory.GetNewItemStats(itemId));
            optionRoller.Roll(userNumber, itemNumber, itemId, ShopRules.ItemShopOptionMode);
            drawn.Add(inventory.ToItemInfo(items.FindOwned(userNumber, itemNumber)!));
        }

        logger.LogInformation("Pendant draw: package {Package} -> [{Items}] for {Price} of {Currency}",
            packageId, string.Join(",", drawn.Select(item => item.ItemId)), package.Price, package.PriceItemId);
        return ProtocolResult.Success(new ItemDrawResponse { Goods = profiles.GetGoods(userNumber), Items = drawn });
    }
}

public sealed record ItemDrawResponse
{
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("item_infos")] public IReadOnlyList<ItemInfo> Items { get; init; } = [];
    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
}
