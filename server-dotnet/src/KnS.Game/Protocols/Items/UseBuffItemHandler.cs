using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Gateway;
using KnS.Game.Players;
using KnS.Game.Protocols.Shop;

namespace KnS.Game.Protocols.Items;

/// <summary>
/// Native Book of Secrets EXP boost rules (dt_etc_setting SpecialItem 1600011..1600015, buff_etc_info type 2).
/// Native text (9600011..9600015) and DrawResult@0xe7c36 define +50% EXP via integer division: base + (base / 2).
/// </summary>
public static class ExpBoostRules
{
    public const long BuffType = 2;
    public const long MinBookItemId = 1600011;
    public const long MaxBookItemId = 1600015;

    public static long Apply(long baseExperience, bool boostActive)
    {
        long gain = Math.Max(0, baseExperience);
        return boostActive ? checked(gain + (gain / 2)) : gain;
    }
}

/// <summary>
/// Protocol 311 (request @0x14def8, parser case @0x15f7f0, NetworkEvent @0x1530fc): consumes a Book of Secrets
/// (1600011-1600015, 1/3/6/12/24h) and activates timed EXP boost buff type 2. Refused when already active (client 9990077).
/// </summary>
public sealed class UseBuffItemHandler(
    PlayerDirectory directory,
    ItemRepository items,
    MasterDataRepository masterData,
    BuffRepository buffs,
    TimeProvider clock) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long itemNumber = request.Parameters.GetInt64("use_item_no");
        ItemRecord? book = items.FindOwned(userNumber, itemNumber);
        if (book is null || book.Locked
            || book.ItemId is < ExpBoostRules.MinBookItemId or > ExpBoostRules.MaxBookItemId
            || masterData.GetSpecialItemValue(book.ItemId) is not { } hours || hours <= 0)
        {
            return ProtocolResult.Refused("invalid Book of Secrets");
        }

        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (buffs.IsActive(userNumber, ExpBoostRules.BuffType, now))
        {
            return ProtocolResult.Refused("EXP boost is already active");
        }

        long endTime = checked(now + hours * 3600);
        buffs.Set(userNumber, ExpBoostRules.BuffType, endTime);
        items.Delete(itemNumber);
        return ProtocolResult.Success(new UseBuffItemResponse(BuffInfo.For(buffs.GetAll(userNumber)), itemNumber));
    }
}

public sealed record UseBuffItemResponse(
    [property: JsonPropertyName("buff_etc_info")] IReadOnlyList<BuffInfo> Buffs,
    [property: JsonPropertyName("use_item_no")] long UsedItemNumber);
