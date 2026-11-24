using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Missions;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Rules;
using KnS.Protocol.Json;

namespace KnS.Game.Protocols.Fellows;

/// <summary>
/// Protocol 310: EXP potions. The browser request at 0x14fd9a carries up to ten item instances and fellow_no;
/// its parser at 0x15f880 reads one fellow_info, fellow_book_info and use_item_no_list.
/// Potion effects come from dt_etc_setting.SpecialItem, not material buy/sell prices.
/// </summary>
public sealed class FellowLevelUpHandler(
    PlayerDirectory directory, FellowRepository fellows, FellowService fellowService,
    FellowMissionService missions, ItemRepository items, MasterDataRepository masterData) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long fellowNumber = request.Parameters.GetInt64("fellow_no");
        FellowUpgradeRecord? fellow = fellows.FindForUpgrade(userNumber, fellowNumber);
        long[] numbers = request.Parameters.GetList("use_item_no_list").Select(LenientJson.ToInt64).ToArray();
        if (fellow is null || FellowRules.IsAngel(fellow.FellowId) || fellow.Level >= fellow.MaxLevel
            || missions.GetBusyFellows(userNumber).Contains(fellowNumber)
            || numbers.Length is < 1 or > 10 || numbers.Distinct().Count() != numbers.Length)
        {
            return ProtocolResult.Refused("cannot level up this ally");
        }

        long gain = 0;
        foreach (long number in numbers)
        {
            ItemRecord? potion = items.FindOwned(userNumber, number);
            if (potion is null || potion.Locked || potion.ItemId is < 1600001 or > 1600003
                || masterData.GetSpecialItemValue(potion.ItemId) is not { } value || value <= 0)
            {
                return ProtocolResult.Refused("invalid EXP potion");
            }
            gain += value;
        }

        fellowService.AwardExperience(userNumber, [fellowNumber], gain);
        foreach (long number in numbers)
        {
            items.Delete(number);
        }
        return ProtocolResult.Success(new FellowLevelUpResponse(
            fellowService.GetFellowInfos(userNumber, new HashSet<long> { fellowNumber }).Single(),
            fellowService.GetBook(userNumber, new HashSet<long> { fellow.FellowId }), numbers));
    }
}

public sealed record FellowLevelUpResponse(
    [property: JsonPropertyName("fellow_info")] FellowInfo Fellow,
    [property: JsonPropertyName("fellow_book_info")] IReadOnlyList<FellowBookEntry> Book,
    [property: JsonPropertyName("use_item_no_list")] IReadOnlyList<long> UsedItems);
