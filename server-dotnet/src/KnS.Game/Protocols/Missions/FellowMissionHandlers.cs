using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Missions;
using KnS.Game.Models;
using KnS.Game.Players;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Missions;

/// <summary>Helpers shared by the ally mission replies.</summary>
internal static class MissionReply
{
    /// <summary>user_reputation_info: persisted country counters; preserve the native contry spelling.</summary>
    public static JsonObject Reputation(IReadOnlyDictionary<long, long> reputation)
    {
        var info = new JsonObject();
        for (int i = 1; i <= 11; i++)
        {
            info[$"contry_reputation{i}"] = reputation.GetValueOrDefault(i);
        }

        return info;
    }

    public static IReadOnlyList<long> Numbers(GatewayRequest request, string name) =>
        request.Parameters.GetList(name).Select(n => long.TryParse(n?.ToString(), out long v) ? v : 0).Where(v => v > 0).ToList();
}

public sealed record MissionRefreshInfo([property: JsonPropertyName("MissionRefreshValue")] long Value);

/// <summary>Protocol 322 (parser case @0x15df00): the mission board, sent at login and when Ally Mission opens.</summary>
public sealed class FellowMissionListHandler(PlayerDirectory directory, FellowMissionService missions) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long freeSoldiers = missions.GetState(userNumber).FreeSoldiersUsed;
        return ProtocolResult.Success(new FellowMissionListResponse
        {
            Missions = missions.GetListedMissions(userNumber).Select(missions.ToInfo).ToList(),
            NextRefresh = new MissionRefreshInfo(missions.NextRefreshPrice(userNumber)),
            Reputation = MissionReply.Reputation(missions.GetReputation(userNumber)),
            FreeSoldierUseCount = freeSoldiers,
        });
    }
}

public sealed record FellowMissionListResponse
{
    [JsonPropertyName("fellow_mission_infos")] public IReadOnlyList<JsonObject> Missions { get; init; } = [];
    [JsonPropertyName("mission_refresh_remain_time")] public long RefreshRemainTime { get; init; }
    [JsonPropertyName("next_mission_refresh_info")] public MissionRefreshInfo NextRefresh { get; init; } = new(0);
    [JsonPropertyName("user_reputation_info")] public JsonObject Reputation { get; init; } = new();
    [JsonPropertyName("free_soldier_use_count")] public long FreeSoldierUseCount { get; init; }
}

/// <summary>Protocol 323 (parser case @0x15ab26): Reset on the board, replacing the missions that are not running, for the native escalating gem price (5..100).</summary>
public sealed class FellowMissionRefreshHandler(
    PlayerDirectory directory, FellowMissionService missions, PlayerProfileService profiles, ILogger<FellowMissionRefreshHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        logger.LogInformation("Mission refresh: {Params}", request.Parameters.ToJsonString());
        long price = missions.NextRefreshPrice(userNumber);
        if (!profiles.TrySpend(userNumber, Rules.CurrencyItems.Gems, price))
        {
            return ProtocolResult.Refused("not enough gems");
        }
        missions.RecordRefresh(userNumber);
        IReadOnlyList<FellowMissionRecord> board = missions.Refresh(userNumber);
        return ProtocolResult.Success(new FellowMissionRefreshResponse
        {
            Missions = board.Select(missions.ToInfo).ToList(),
            Goods = profiles.GetGoods(userNumber),
            NextRefresh = new MissionRefreshInfo(missions.NextRefreshPrice(userNumber)),
        });
    }
}

public sealed record FellowMissionRefreshResponse
{
    [JsonPropertyName("fellow_mission_infos")] public IReadOnlyList<JsonObject> Missions { get; init; } = [];
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("next_mission_refresh_info")] public MissionRefreshInfo NextRefresh { get; init; } = new(0);
}

/// <summary>Protocol 324 (parser case @0x15ddae): send allies on a mission, <c>{mission_id, fellow_no_list, soldier_id_list}</c>.</summary>
public sealed class FellowMissionStartHandler(
    PlayerDirectory directory, FellowMissionService missions, PlayerProfileService profiles, ILogger<FellowMissionStartHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        logger.LogInformation("Mission start: {Params}", request.Parameters.ToJsonString());
        long missionId = request.Parameters.GetInt64("mission_id");
        IReadOnlyList<long> allies = MissionReply.Numbers(request, "fellow_no_list");
        IReadOnlyList<long> soldiers = MissionReply.Numbers(request, "soldier_id_list");
        if (soldiers.Count > 10 || soldiers.Any(id => id is < 1 or > 5))
        {
            return ProtocolResult.Refused("invalid mercenaries");
        }
        (long price, long freeUsed) = FellowMissionService.SoldierPrice(soldiers, missions.GetState(userNumber).FreeSoldiersUsed);
        if (profiles.GetAccount(userNumber).Gems < price)
        {
            return ProtocolResult.Refused("not enough gems");
        }

        // The request lists exactly the selected allies (request builder @0x14e19a); never add others.
        FellowMissionRecord? started = missions.Start(userNumber, missionId, allies, soldiers);
        if (started is null)
        {
            return ProtocolResult.Refused("cannot start this mission");
        }

        // A refusal is a normal transaction result, so spending before validation would still commit.
        profiles.AddCurrency(userNumber, gems: -price);
        if (freeUsed > 0)
        {
            missions.UseFreeSoldiers(userNumber, freeUsed);
        }

        return ProtocolResult.Success(new FellowMissionStartResponse
        {
            Mission = missions.ToInfo(started),
            Goods = profiles.GetGoods(userNumber),
            FreeSoldierUseCount = missions.GetState(userNumber).FreeSoldiersUsed,
        });
    }
}

public sealed record FellowMissionStartResponse
{
    [JsonPropertyName("fellow_mission_info")] public JsonObject Mission { get; init; } = new();
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("free_soldier_use_count")] public long FreeSoldierUseCount { get; init; }
}

/// <summary>Protocol 325 (parser case @0x160d70): collect finished missions, <c>{mission_id_list, use_cash}</c>.</summary>
public sealed class FellowMissionClaimHandler(
    PlayerDirectory directory,
    FellowMissionService missions,
    FellowService fellows,
    FellowItemService fellowItems,
    FellowItemRepository fellowItemRepository,
    PlayerProfileService profiles,
    ILogger<FellowMissionClaimHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        logger.LogInformation("Mission claim: {Params}", request.Parameters.ToJsonString());
        IReadOnlyList<long> ids = MissionReply.Numbers(request, "mission_id_list");
        (IReadOnlyList<FellowMissionRecord> succeeded, IReadOnlyList<FellowMissionRecord> failed, IReadOnlyList<long> awardedGear) = missions.Claim(userNumber, ids, request.Parameters.GetInt64("use_cash") == 1);
        return ProtocolResult.Success(new FellowMissionClaimResponse
        {
            Succeeded = succeeded.Select(missions.ToInfo).ToList(),
            Failed = failed.Select(missions.ToFailedInfo).ToList(),
            Missions = missions.GetBoard(userNumber).Select(missions.ToInfo).ToList(),
            Allies = fellows.GetFellowInfos(userNumber),
            FellowItems = awardedGear.Select(number => fellowItems.ToInfo(fellowItemRepository.Find(userNumber, number)!)).ToList(),
            Goods = profiles.GetGoods(userNumber),
            Reputation = MissionReply.Reputation(missions.GetReputation(userNumber)),
        });
    }
}

public sealed record FellowMissionClaimResponse
{
    [JsonPropertyName("success_fellow_mission_infos")] public IReadOnlyList<JsonObject> Succeeded { get; init; } = [];
    [JsonPropertyName("fail_fellow_mission_infos")] public IReadOnlyList<JsonObject> Failed { get; init; } = [];
    [JsonPropertyName("fellow_mission_infos")] public IReadOnlyList<JsonObject> Missions { get; init; } = [];
    [JsonPropertyName("fellow_info")] public IReadOnlyList<FellowInfo> Allies { get; init; } = [];
    [JsonPropertyName("fellow_item_infos")] public IReadOnlyList<FellowItemInfo> FellowItems { get; init; } = [];
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("user_reputation_info")] public JsonObject Reputation { get; init; } = new();
}

/// <summary>
/// Protocol 326 starts tutorial mission 6220000. Keep it separate from normal board missions.
/// </summary>
public sealed class FellowMissionFinishHandler(PlayerDirectory directory, FellowMissionService missions) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        FellowMissionRecord? started = missions.StartTutorial(user, request.Parameters.GetInt64("mission_id"),
            MissionReply.Numbers(request, "fellow_no_list"), MissionReply.Numbers(request, "soldier_id_list"));
        return started is null ? ProtocolResult.Refused("cannot start tutorial mission")
            : ProtocolResult.Success(new { fellow_mission_info = missions.ToInfo(started) });
    }
}

public sealed class FellowMissionCancelHandler(PlayerDirectory directory, FellowMissionService missions) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        FellowMissionRecord? claimed = missions.ClaimTutorial(user, request.Parameters.GetInt64("mission_id"));
        return claimed is null ? ProtocolResult.Refused("cannot claim tutorial mission")
            : ProtocolResult.Success(new { fellow_mission_info = missions.ToInfo(claimed) });
    }
}
