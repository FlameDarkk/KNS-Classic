using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Randomness;
using KnS.Game.Rules;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Fellows;

/// <summary>
/// Protocol 309: Ally Evolution {fellow_no, used_fellow_no} (request case @0x14fde0, parser @0x15d594, sent @0xad516). An angel
/// (Evolution Material Ally) is used up; on success fellow_awaken_level rises by one and unlocks the next of the ally's evolution
/// skills (master ints 97-100). The client allows it while fellow_awaken_level is below master int 96 (@0xad524). The reply is the
/// power-up shape; upgrade_result 0 shows "Evolution Failed". Evolutions give no Power-up Points.
/// </summary>
/// <remarks>
/// Cost: master_fellow_upgrade c5 for the ally's grade (10000 / 25000 / 50000 for 4-6 stars). The chance uses the power-up rate of
/// the grade gap (an angel of the same or a higher grade: 100%); the original evolution rate formula is not mapped.
/// </remarks>
public sealed class FellowEvolveHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    FellowService fellowService,
    FellowRepository fellows,
    FellowItemRepository fellowItems,
    KnS.Game.Missions.FellowMissionService missions,
    MasterDataRepository masterData,
    IRandomSource random,
    ILogger<FellowEvolveHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        long targetNumber = request.Parameters.GetInt64("fellow_no");
        long materialNumber = request.Parameters.GetInt64("used_fellow_no");
        FellowUpgradeRecord? target = fellows.FindForUpgrade(user, targetNumber);
        FellowUpgradeRecord? material = fellows.FindForUpgrade(user, materialNumber);
        HashSet<long> slotted = fellows.GetFilledSlots(user).Select(slot => slot.FellowNumber).ToHashSet();
        HashSet<long> busy = missions.GetBusyFellows(user);
        (long maxEvolutions, IReadOnlyList<long> skills) = target is null ? (0, []) : fellows.GetEvolutionData(target.FellowId);
        long cost = target is null ? 0 : masterData.GetFellowEvolutionGold(target.Grade);

        if (target is null || material is null || targetNumber == materialNumber || FellowRules.IsAngel(target.FellowId)
            || !FellowRules.IsAngel(material.FellowId) || material.Locked != 0 || slotted.Contains(materialNumber)
            || busy.Contains(targetNumber) || busy.Contains(materialNumber)
            || target.Awaken >= maxEvolutions || cost <= 0 || !profiles.TrySpend(user, CurrencyItems.Gold, cost))
        {
            logger.LogInformation("Fellow evolve refused (fellow_no {Target}, used {Material})", targetNumber, materialNumber);
            return ProtocolResult.Refused("cannot evolve");
        }

        var attempt = ApplyAttempt(user, target, material, cost, skills, masterData.GetFellowPowerUpRates());
        return ProtocolResult.Success(new FellowEvolveResponse
        {
            Goods = profiles.GetGoods(user),
            Fellow = fellowService.GetFellowInfos(user, new HashSet<long> { targetNumber }).Single(),
            UsedFellowNumber = materialNumber,
            UpgradeResult = attempt.Success ? 1 : 0,
            UnequippedFellowItems = attempt.FreedItems,
        });
    }

    internal static long Percent(FellowUpgradeRecord target, FellowUpgradeRecord material, IReadOnlyList<FellowPowerUpRate> rates)
    {
        long gap = Math.Max(0, target.Grade - material.Grade);
        return gap == 0 ? 100 : rates.FirstOrDefault(rate => rate.GradeGap == gap)?.SuccessPercent ?? 0;
    }

    // Both callers validate eligibility and charge evolution gold before applying the roll.
    internal (FellowUpgradeRecord Target, long Percent, bool Success, IReadOnlyList<long> FreedItems) ApplyAttempt(
        long user, FellowUpgradeRecord target, FellowUpgradeRecord material, long cost,
        IReadOnlyList<long> skills, IReadOnlyList<FellowPowerUpRate> rates)
    {
        long percent = Percent(target, material, rates);
        bool success = random.NextBelow(100) < percent;
        if (success)
        {
            long skill = skills.ElementAtOrDefault((int)target.Awaken);
            fellows.AddEvolution(target.FellowNumber, skill == FellowRules.MaxLevelEvolutionSkill ? FellowRules.MaxLevelPerEvolutionSkill : 0);
            target = target with { Awaken = target.Awaken + 1 };
        }

        IReadOnlyList<long> freedItems = fellowItems.ReleaseFromFellow(user, material.FellowNumber);
        fellows.Delete(material.FellowNumber);
        logger.LogInformation("Fellow evolve: fellow_no {Target} {Result} at {Percent}% ({Gold} G), used {Material}",
            target.FellowNumber, success ? "succeeded" : "failed", percent, cost, material.FellowNumber);
        return (target, percent, success, freedItems);
    }
}

public sealed record FellowEvolveResponse
{
    [JsonPropertyName("unequip_fellow_item_no_list")] public IReadOnlyList<long> UnequippedFellowItems { get; init; } = [];
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("fellow_info")] public FellowInfo Fellow { get; init; } = new();
    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
    [JsonPropertyName("used_fellow_no")] public long UsedFellowNumber { get; init; }
    [JsonPropertyName("upgrade_result")] public long UpgradeResult { get; init; }
}

/// <summary>
/// Protocol 319: change talent {fellow_no, attribute_no} (request case @0x14f04a, parser @0x15f7c8; prompts 9990274 / 9990275).
/// Costs AttributeResetValue gems (10); rolls a new mission skill family from the ally's five and a level by
/// <see cref="FellowRules.TalentLevelWeights"/>. Reply: one fellow_info and update_item_etc_info.
/// </summary>
public sealed class FellowTalentResetHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    FellowService fellowService,
    FellowRepository fellows,
    FellowMissionRepository missionData,
    IRandomSource random) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        long number = request.Parameters.GetInt64("fellow_no");
        long slot = request.Parameters.GetInt64("attribute_no");
        FellowRecord? fellow = fellows.GetOwned(user).FirstOrDefault(row => row.FellowNumber == number);
        if (fellow is null || slot is not (1 or 2) || FellowRules.IsAngel(fellow.FellowId))
            return ProtocolResult.Refused("cannot change talent");

        IReadOnlyList<long> talents = fellowService.GetTalents(fellow);
        long other = talents[(int)(2 - slot)];
        List<long> choices = missionData.GetAllSkills(fellow.FellowId).Select(skill => skill / 10).Distinct()
            .Where(family => family != other / 10).ToList();
        if (talents[(int)slot - 1] <= 0 || choices.Count == 0 || !profiles.TrySpend(user, CurrencyItems.Gems, FellowRules.TalentResetGems))
            return ProtocolResult.Refused("cannot change talent");

        long level = random.PickWeighted(
            FellowRules.TalentLevelWeights.Select(entry => entry.Level).ToList(), FellowRules.TalentLevelWeights.Select(entry => entry.Weight).ToList());
        fellows.SetTalent(number, slot, random.Pick(choices) * 10 + level);
        return ProtocolResult.Success(new FellowTalentResetResponse(
            fellowService.GetFellowInfos(user, new HashSet<long> { number }).Single(), profiles.GetGoods(user)));
    }
}

public sealed record FellowTalentResetResponse(
    [property: JsonPropertyName("fellow_info")] FellowInfo Fellow,
    [property: JsonPropertyName("update_item_etc_info")] IReadOnlyList<GoodsEntry> Goods);
