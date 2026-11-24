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
/// Protocol 85: the Black Market list (parser case @0x15fa4c), <c>{use_cash}</c>: 0 when the screen opens, 1 from the Refresh button. The stock
/// lives an hour; the screen's timer is <c>user_info.user_next_shop_update_time</c>. Offers are item rows in the shop's claimed state.
/// </summary>
public sealed class BlackMarketHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    InventoryService inventory,
    ItemRepository items,
    BlackMarketRepository blackMarket,
    ItemOptionRoller optionRoller,
    HeroGearRules heroGear,
    MasterDataRepository masterData,
    IRandomSource random,
    TimeProvider clock,
    ILogger<BlackMarketHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        Player player = directory.ResolveCurrent(request);
        long userNumber = player.UserNumber;
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        bool paidRefresh = request.Parameters.GetInt64("use_cash") != 0;
        if (paidRefresh && !profiles.TrySpend(userNumber, CurrencyItems.Gems, ShopRules.BlackMarketRefreshGems))
        {
            return ProtocolResult.Refused("cannot refresh");
        }

        if (paidRefresh || blackMarket.GetNextRefresh(userNumber) <= now)
        {
            items.ClearShopStock(userNumber);
            // What this hero can wear: costumes, pets and other heroes' lines stay out. Shop/event specials (c31 = 1, fixed
            // 15,000-1,500,000 G prices) stay out. Grades follow progress: per equipment range, the window up to the best grade
            // the player's cleared stages drop or their owned gear has (restoration choice; no native stock rule exists).
            IReadOnlyList<long> series = heroGear.GetSeries(profiles.GetAccount(userNumber).HeroPcid);
            long progressGrade = Math.Max(masterData.GetClearedDropGradeMax(userNumber), masterData.GetOwnedEquipmentGradeMax(userNumber));
            List<BuyableItem> pool = ShopRules.BlackMarketPool(
                masterData.GetBuyableItems().Where(item => item.Special == 0 && heroGear.CanEquip(item.ItemId, series)),
                progressGrade);
            foreach (BuyableItem item in random.PickMany(pool, ShopRules.BlackMarketSize))
            {
                long itemNumber = items.AddShopStock(userNumber, item.ItemId, inventory.GetNewItemStats(item.ItemId));
                optionRoller.Roll(userNumber, itemNumber, item.ItemId, ShopRules.ItemShopOptionMode);
            }

            blackMarket.SetNextRefresh(userNumber, now + ShopRules.BlackMarketLifetimeSeconds);
            logger.LogInformation("Black market: new stock for user {User} (paid {Paid})", userNumber, paidRefresh);
        }

        return ProtocolResult.Success(new BlackMarketResponse
        {
            UserInfo = profiles.BuildUserInfo(player),
            Goods = profiles.GetGoods(userNumber),
            Stock = items.GetShopStock(userNumber).Select(item => inventory.ToItemInfo(item)).ToList(),
        });
    }
}

public sealed record BlackMarketResponse
{
    [JsonPropertyName("user_info")] public required UserInfo UserInfo { get; init; }
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("shop_info")] public IReadOnlyList<ItemInfo> Stock { get; init; } = [];
}

/// <summary>
/// Protocol 87: buys one Black Market offer (parser case @0x15fba4): <c>{item_no}</c>. The reply's <c>item_info</c> is the new bag item; <c>item_no</c> names
/// the offer to take off the list.
/// </summary>
public sealed class BlackMarketBuyHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    InventoryService inventory,
    ItemRepository items,
    MasterDataRepository masterData,
    ILogger<BlackMarketBuyHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        Player player = directory.ResolveCurrent(request);
        long userNumber = player.UserNumber;
        long itemNumber = request.Parameters.GetInt64("item_no");
        ItemRecord? offer = items.GetShopStock(userNumber).FirstOrDefault(item => item.ItemNumber == itemNumber);
        long price = offer is null ? 0 : masterData.GetBuyPrice(offer.ItemId) ?? 0;
        if (offer is null || price <= 0 || !profiles.TrySpend(userNumber, CurrencyItems.Gold, price))
        {
            logger.LogInformation("Black market buy refused: item_no {Item} price {Price}", itemNumber, price);
            return ProtocolResult.Refused("cannot buy");
        }

        ItemRecord bought = items.Claim(userNumber, itemNumber)!;
        logger.LogInformation("Black market: user {User} bought {Item} for {Price} G", userNumber, offer.ItemId, price);
        return ProtocolResult.Success(new BlackMarketBuyResponse
        {
            UserInfo = profiles.BuildUserInfo(player),
            Goods = profiles.GetGoods(userNumber),
            Item = inventory.ToItemInfo(bought),
            ItemNumber = itemNumber,
        });
    }
}

public sealed record BlackMarketBuyResponse
{
    [JsonPropertyName("user_info")] public required UserInfo UserInfo { get; init; }
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("item_info")] public required ItemInfo Item { get; init; }
    [JsonPropertyName("item_no")] public long ItemNumber { get; init; }
    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
}
