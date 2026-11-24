using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Missions;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Randomness;
using KnS.Game.Rules;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Fellows;

/// <summary>
/// Protocol 303: ally power-up (request case @0x14e878, parser @0x160290, post-handler @0x154042). The material ally is used up;
/// the roll, the cost and the Power-up Points follow DrawFellowUpgrade @0xa8158.
/// The client deletes the material from its own list when the animation ends, so the reply does not list it.
/// </summary>
public sealed class FellowPowerUpHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    FellowService fellowService,
    FellowRepository fellows,
    FellowItemRepository fellowItems,
    FellowMissionService missions,
    MasterDataRepository masterData,
    IRandomSource random,
    DailyQuestRepository quests,
    TimeProvider clock,
    ILogger<FellowPowerUpHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long targetNumber = request.Parameters.GetInt64("fellow_no");
        long materialNumber = request.Parameters.GetInt64("used_fellow_no");

        FellowUpgradeRecord? target = fellows.FindForUpgrade(userNumber, targetNumber);
        FellowUpgradeRecord? material = fellows.FindForUpgrade(userNumber, materialNumber);
        HashSet<long> slotted = fellows.GetFilledSlots(userNumber).Select(slot => slot.FellowNumber).ToHashSet();
        HashSet<long> busy = missions.GetBusyFellows(userNumber);
        FellowUpgradeCosts? costs = target is null
            ? null
            : masterData.GetFellowUpgradeCosts(target.Grade, Math.Min(target.Upgrade + 1, FellowRules.MaxUpgrade));
        long cost = Math.Max(0, costs?.PowerUpGold ?? 0);

        string? refusal = null;
        if (target is null || material is null || costs is null || targetNumber == materialNumber
            || FellowRules.IsAngel(target.FellowId) || FellowRules.IsAngel(material.FellowId)
            || material.Locked != 0 || slotted.Contains(materialNumber)
            || busy.Contains(targetNumber) || busy.Contains(materialNumber))
        {
            refusal = "invalid power up";
        }
        else if (profiles.GetAccount(userNumber).Gold < cost)
        {
            refusal = "cannot power up";
        }

        if (refusal is not null)
        {
            logger.LogInformation("Fellow power up refused: {Reason} (fellow_no {Target}, used {Material})", refusal, targetNumber, materialNumber);
            return ProtocolResult.Refused(refusal);
        }

        var attempt = ApplyAttempt(userNumber, target!, material!, cost, masterData.GetFellowPowerUpRates());
        return ProtocolResult.Success(new FellowPowerUpResponse
        {
            Goods = profiles.GetGoods(userNumber),
            Fellow = fellowService.GetFellowInfos(userNumber, new HashSet<long> { targetNumber }).Single(),
            UsedFellowNumber = materialNumber,
            DailyQuest = Quests.DailyQuestInfo.Current(quests, clock, userNumber, 5300008),
            UpgradeResult = attempt.Success ? 1 : 0,
            UnequippedFellowItems = attempt.FreedItems,
        });
    }

    private static FellowPowerUpRate? Rate(FellowUpgradeRecord target, FellowUpgradeRecord material,
        IReadOnlyList<FellowPowerUpRate> rates) =>
        rates.FirstOrDefault(rate => rate.GradeGap == Math.Max(0, target.Grade - material.Grade));

    private static long Percent(FellowUpgradeRecord target, FellowUpgradeRecord material,
        IReadOnlyList<FellowPowerUpRate> rates) =>
        FellowRules.PowerUpPercent(target.Grade, material.Grade, target.Upgrade, target.FailCount,
            Rate(target, material, rates)?.SuccessPercent ?? 0);

    private (FellowUpgradeRecord Target, long Percent, bool Success, IReadOnlyList<long> FreedItems) ApplyAttempt(
        long user, FellowUpgradeRecord target, FellowUpgradeRecord material, long cost, IReadOnlyList<FellowPowerUpRate> rates)
    {
        long percent = Percent(target, material, rates);
        bool success = random.NextBelow(100) < percent;
        long upgrade = success ? Math.Min(FellowRules.MaxUpgrade, target.Upgrade + 1) : target.Upgrade;
        long failCount = success ? 0 : target.FailCount + (Rate(target, material, rates)?.FailPoints ?? 0);
        long bonus = Math.Min(FellowRules.AttackBonusCap, target.AttackBonus + material.AttackBonus);
        fellows.SetUpgradeState(target.FellowNumber, upgrade, failCount, bonus);
        IReadOnlyList<long> freedItems = fellowItems.ReleaseFromFellow(user, material.FellowNumber);
        fellows.Delete(material.FellowNumber);
        profiles.AddCurrency(user, gold: -cost);
        quests.Add(user, clock.GetUtcNow().ToUnixTimeSeconds() / 86400, 5300008, 1);

        logger.LogInformation("Fellow power up: fellow_no {Target} {Result} at {Percent}% ({Gold} G), now +{Upgrade}, points {Points}, used {Material}",
            target.FellowNumber, success ? "succeeded" : "failed", percent, cost, upgrade, failCount, material.FellowNumber);
        return (target with { Upgrade = upgrade, FailCount = failCount, AttackBonus = bonus }, percent, success, freedItems);
    }
}

public sealed record FellowPowerUpResponse
{
    [JsonPropertyName("daily_quest_info")] public required Quests.DailyQuestInfo DailyQuest { get; init; }
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];

    /// <summary>The target after the power-up: one object, not a list (the parser calls ParsingDataToFellowInfo directly).</summary>
    [JsonPropertyName("fellow_info")] public FellowInfo Fellow { get; init; } = new();

    [JsonPropertyName("used_fellow_no")] public long UsedFellowNumber { get; init; }

    /// <summary>1 = the power-up worked, 0 = it failed (the failure art and the Power-up Points message).</summary>
    [JsonPropertyName("upgrade_result")] public long UpgradeResult { get; init; }

    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
    [JsonPropertyName("unequip_fellow_item_no_list")] public IReadOnlyList<long> UnequippedFellowItems { get; init; } = [];
}
