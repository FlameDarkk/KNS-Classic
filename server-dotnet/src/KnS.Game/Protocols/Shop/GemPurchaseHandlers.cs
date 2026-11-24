using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Inventory;
using KnS.Game.Players;
using KnS.Game.Rewards;
using KnS.Game.Rules;

namespace KnS.Game.Protocols.Shop;

/// <summary>Protocol 109 buys a browser shop gold bundle with gems.</summary>
public sealed class GoldShopHandler(PlayerDirectory directory, PlayerProfileService profiles, MasterDataRepository masterData) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        GoldShopProduct? product = masterData.GetGoldShopProduct(request.Parameters.GetInt64("product_id"));
        if (product is null || product.Gold <= 0 || product.BonusGold < 0 || product.GemPrice <= 0
            || !profiles.TrySpend(user, CurrencyItems.Gems, product.GemPrice))
            return ProtocolResult.Refused("cannot buy gold");
        profiles.AddCurrency(user, gold: checked(product.Gold + product.BonusGold));
        return ProtocolResult.Success(new GemPurchaseResponse { Goods = profiles.GetGoods(user) });
    }
}

/// <summary>Protocol 105 restores the stamina bar for the configured gem price.</summary>
public sealed class StaminaRefillHandler(PlayerDirectory directory, PlayerProfileService profiles, PlayerRepository players, MasterDataRepository masterData, TimeProvider clock) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        Player player = directory.ResolveCurrent(request);
        long user = player.UserNumber;
        (long stamina, _) = profiles.GetStamina(user);
        long max = profiles.GetStaminaLimits(user).Max;
        Price? price = masterData.GetStorePrice("StaminaRecovery");
        if (stamina >= max || price is null || price.PriceItemId != CurrencyItems.Gems || price.Amount <= 0
            || !profiles.TrySpend(user, price.PriceItemId, price.Amount))
            return ProtocolResult.Refused("cannot refill stamina");
        players.SetStamina(user, max, clock.GetUtcNow().ToUnixTimeSeconds());
        return ProtocolResult.Success(new GemPurchaseResponse { UserInfo = profiles.BuildUserInfo(player), Goods = profiles.GetGoods(user) });
    }
}

public sealed record GemPurchaseResponse
{
    [JsonPropertyName("user_info")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public UserInfo? UserInfo { get; init; }
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
}

/// <summary>Protocol 904 returns a purchased special item and the updated wallet.</summary>
public sealed class FriendshipShopBuyHandler(PlayerDirectory directory, PlayerProfileService profiles, MasterDataRepository masterData,
    ItemRepository items, InventoryService inventory, RewardGrantService rewards) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        FriendshipShopProduct? product = masterData.GetFriendshipShopProduct(request.Parameters.GetInt64("product_id"));
        if (product is null || product.Price <= 0)
            return ProtocolResult.Refused("cannot buy this product");
        if (product.PriceItemId == CurrencyItems.FriendshipPoints)
            return BuyWithFriendshipPoints(user, product);
        // These are the three gem-priced special books, each delivered as one inventory instance.
        if (product.PriceItemId != CurrencyItems.Gems
            || product.RewardItemId is < 1600011 or > 1600013 || product.RewardCount != 1
            || !profiles.TrySpend(user, product.PriceItemId, product.Price))
            return ProtocolResult.Refused("cannot buy this product");
        long number = items.Add(user, product.RewardItemId, claimed: true, inventory.GetNewItemStats(product.RewardItemId));
        return ProtocolResult.Success(new FriendshipShopBuyResponse(profiles.GetGoods(user), inventory.ToItemInfo(items.FindOwned(user, number)!)));
    }

    /// <summary>
    /// Friendship-point products: every check runs before the spend, then the reward is paid by the shared grant service.
    /// Stamina and allies reach the client through update_item_etc_info; 904 has no fellow reply, so item_info is omitted
    /// unless a material was granted (the parser accepts a missing item_info).
    /// </summary>
    private ProtocolResult BuyWithFriendshipPoints(long user, FriendshipShopProduct product)
    {
        if (product.RewardCount is <= 0 or > RewardGrantService.MaxOwnedPerGrant
            || rewards.Classify(product.RewardItemId) is RewardKind.Unknown
            || !profiles.TrySpend(user, CurrencyItems.FriendshipPoints, product.Price))
            return ProtocolResult.Refused("cannot buy this product");
        GrantedRewards granted = rewards.Grant(user, product.RewardItemId, product.RewardCount);
        ItemRecord? item = granted.ItemNumbers.Count > 0 ? items.FindOwned(user, granted.ItemNumbers[0]) : null;
        return ProtocolResult.Success(new FriendshipShopBuyResponse(profiles.GetGoods(user), item is null ? null : inventory.ToItemInfo(item)));
    }
}

public sealed record FriendshipShopBuyResponse(
    [property: JsonPropertyName("update_item_etc_info")] IReadOnlyList<GoodsEntry> Goods,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] [property: JsonPropertyName("item_info")] ItemInfo? Item);
