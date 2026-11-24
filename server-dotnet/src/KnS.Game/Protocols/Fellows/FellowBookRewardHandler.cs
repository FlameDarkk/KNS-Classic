using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Rules;

namespace KnS.Game.Protocols.Fellows;

/// <summary>
/// Protocol 308: the "Collect" button of the Collection of Allies. Request {fellow_id} (case @0x14fdce);
/// parser @0x15b00a reads one fellow_book_info and update_item_etc_info. Pays
/// <see cref="FellowRules.MaxLevelRewardGems"/> gems once per ally id after reaching the collection's base level cap,
/// including allies that have since been sold or consumed.
/// </summary>
public sealed class FellowBookRewardHandler(
    PlayerDirectory directory, PlayerProfileService profiles, FellowRepository fellows, FellowService fellowService) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        long fellowId = request.Parameters.GetInt64("fellow_id");
        if (!fellows.HasReachedBookMaxLevel(user, fellowId)
            || !fellows.TryClaimBookReward(user, fellowId))
        {
            return ProtocolResult.Refused("cannot collect this reward");
        }

        profiles.AddCurrency(user, gems: FellowRules.MaxLevelRewardGems);
        return ProtocolResult.Success(new FellowBookRewardResponse(
            fellowService.GetBook(user, new HashSet<long> { fellowId }).Single(), profiles.GetGoods(user)));
    }
}

public sealed record FellowBookRewardResponse(
    [property: JsonPropertyName("fellow_book_info")] FellowBookEntry Book,
    [property: JsonPropertyName("update_item_etc_info")] IReadOnlyList<GoodsEntry> Goods);
