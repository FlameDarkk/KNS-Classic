using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Rules;

namespace KnS.Game.Protocols.Shop;

/// <summary>
/// Hero Point stamina upgrades: 901 "▲ MAX Stamina" (+2 MAX Stamina each) and 902 "▼ Recovery
/// Time" (10 seconds less per point each), ten purchases each at 2500 Hero Points (dt_shop_pkg_140
/// table 10). The parsers (cases @0x15b53e / @0x15b512) read user_info and update_item_etc_info;
/// the post-handlers then show "%d + %d (%d/%d)" / "%d sec - %d sec (%d/%d)" from the new counts.
/// </summary>
public sealed class StaminaUpgradeHandler(bool maxStamina, PlayerDirectory directory, PlayerProfileService profiles, BuffRepository buffs)
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        Player player = directory.ResolveCurrent(request);
        long user = player.UserNumber;
        (long maxCount, long timeCount) = buffs.GetStaminaUpgrades(user);
        if ((maxStamina ? maxCount : timeCount) >= PlayerRules.StaminaUpgradeLimit
            || !profiles.TrySpend(user, CurrencyItems.HeroPoints, PlayerRules.StaminaUpgradePrice))
        {
            return ProtocolResult.Refused("cannot buy this stamina upgrade");
        }

        // Settle the bar at the old rate before the capacity or refill period changes.
        (long stamina, long lastGain) = profiles.GetStamina(user);
        buffs.AddStaminaUpgrade(user, maxStamina);
        profiles.SetStamina(user, stamina, lastGain);
        return ProtocolResult.Success(new StaminaUpgradeResponse(profiles.BuildUserInfo(player), profiles.GetGoods(user)));
    }
}

public sealed class MaxStaminaUpgradeHandler(PlayerDirectory directory, PlayerProfileService profiles, BuffRepository buffs) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request) => new StaminaUpgradeHandler(true, directory, profiles, buffs).Handle(request);
}

public sealed class StaminaRecoveryUpgradeHandler(PlayerDirectory directory, PlayerProfileService profiles, BuffRepository buffs) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request) => new StaminaUpgradeHandler(false, directory, profiles, buffs).Handle(request);
}

public sealed record StaminaUpgradeResponse(
    [property: JsonPropertyName("user_info")] UserInfo UserInfo,
    [property: JsonPropertyName("update_item_etc_info")] IReadOnlyList<GoodsEntry> Goods);
