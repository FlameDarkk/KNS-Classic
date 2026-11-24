using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Protocols.Quests;
using KnS.Game.Rules;

namespace KnS.Game.Protocols.Shop;

/// <summary>
/// 903 buys a Hero Point product (dt_shop_pkg_140 table 8). The parser (case @0x15b5e0) sets the
/// post-handler mode from the reply's fields: new_fellow_info = an ally (mode 0, one book object);
/// none = goods only (mode 1, UpdateUserGoods); buff_etc_info present, even empty = Auto Skill
/// (mode 5: UpdateUserGoods, then the Auto Skill unlock at gameInfo+0x33b8 and its popup).
/// </summary>
/// <remarks>
/// Supported: purchase type 0 (fixed angels), 4 (ten counted collectibles) and 5 (Auto Skill,
/// text 9994121). Types 2 / 3 (MAX Stamina / Recovery Time) are bought through 901 / 902
/// (<see cref="StaminaUpgradeHandler"/>); packages (10 / 20) are draws. Those stay refused here without spending.
/// </remarks>
public sealed class HeroPointShopBuyHandler(PlayerDirectory directory, PlayerProfileService profiles,
    MasterDataRepository master, FellowService fellows, KnS.Game.Stages.StoryService storyService,
    GoodsRepository goods, BuffRepository buffs) : IProtocolHandler
{
    public const long CollectiblesPurchaseType = 4;
    public const long AutoSkillPurchaseType = 5;

    /// <summary>buff_etc_info type that ParsingData (60) and the 903 post-handler treat as Auto Skill.</summary>
    public const long AutoSkillBuffType = 5;

    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        HeroPointShopProduct? product = master.GetHeroPointShopProduct(request.Parameters.GetInt64("product_id"));
        if (product is null || product.PriceItemId != CurrencyItems.HeroPoints || product.Price <= 0 || !IsSupported(product)
            || (product.PurchaseType == AutoSkillPurchaseType && buffs.Has(user, AutoSkillBuffType))
            || !profiles.TrySpend(user, product.PriceItemId, product.Price))
            return ProtocolResult.Refused("cannot buy this Hero Point product");

        FellowInfo? fellow = null;
        FellowBookEntry? book = null;
        IReadOnlyList<BuffInfo>? buffInfo = null;
        switch (product.PurchaseType)
        {
            case 0:
                long number = fellows.Grant(user, product.RewardItemId);
                fellow = fellows.GetFellowInfos(user, new HashSet<long> { number }).Single();
                book = fellows.GetBook(user, new HashSet<long> { product.RewardItemId }).Single();
                break;
            case CollectiblesPurchaseType:
                goods.Add(user, product.RewardItemId, product.RewardCount);
                break;
            case AutoSkillPurchaseType:
                buffs.TryAdd(user, AutoSkillBuffType, 0);
                buffInfo = BuffInfo.For(buffs.GetAll(user));
                break;
        }

        StoryRow story = storyService.Current(user);
        return ProtocolResult.Success(new HeroPointShopBuyResponse(fellow, book, buffInfo, profiles.GetGoods(user), story));
    }

    private static bool IsSupported(HeroPointShopProduct product) => product.PurchaseType switch
    {
        0 => product.RewardItemId is 110003 or 110004 && product.RewardCount == 1,
        // ParsingDataToUserGoods keeps the collectibles 1901001-1901008.
        CollectiblesPurchaseType => product.RewardItemId is >= 1901001 and <= 1901008 && product.RewardCount > 0,
        AutoSkillPurchaseType => true,
        _ => false,
    };
}

/// <summary>A buff_etc_info row: ParsingData (60) reads type 5 as Auto Skill and type 2's end_date_timestamp.</summary>
public sealed record BuffInfo(
    [property: JsonPropertyName("type")] long Type,
    [property: JsonPropertyName("end_date_timestamp")] long EndDate)
{
    public static IReadOnlyList<BuffInfo> For(IReadOnlyList<BuffRecord> buffs) =>
        buffs.Select(buff => new BuffInfo(buff.BuffType, buff.EndTime)).ToList();
}

/// <summary>Only the fields of the bought product's kind are written: the parser branches on their presence.</summary>
public sealed record HeroPointShopBuyResponse(
    [property: JsonPropertyName("new_fellow_info"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FellowInfo? Fellow,
    [property: JsonPropertyName("fellow_book_info"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FellowBookEntry? Book,
    [property: JsonPropertyName("buff_etc_info"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BuffInfo>? Buffs,
    [property: JsonPropertyName("update_item_etc_info")] IReadOnlyList<GoodsEntry> Goods,
    [property: JsonPropertyName("story_info")] StoryRow Story)
{
    [JsonPropertyName("story_card_infos")] public IReadOnlyList<object> StoryCards { get; init; } = [];
}
