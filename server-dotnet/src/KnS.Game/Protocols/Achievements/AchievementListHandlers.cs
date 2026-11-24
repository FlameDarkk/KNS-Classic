using System.Globalization;
using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Gateway;
using KnS.Game.Players;

namespace KnS.Game.Protocols.Achievements;

/// <summary>
/// Protocol 121 (parser case @0x15da36), sent at login and from the Achievements popup: the in-progress counters,
/// <c>achievement_infos</c> read by <c>ParsingDataToAchieveDoing</c>@0x14b1f4 (idx, type, sub_type, count). Unhandled,
/// the bare reply left every client-counted counter at 0 after a reload. Sends the stored totals plus the level
/// counter (type 40000).
/// </summary>
public sealed class AchievementListHandler(
    PlayerDirectory directory, AchievementRepository achievements, PlayerRepository players) : IProtocolHandler
{
    private const long LevelAchievementType = 40000;

    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        var infos = new List<AchievementDoingInfo>
        {
            new(0, LevelAchievementType, 0, players.FindAccount(user)?.Level ?? 1),
        };
        infos.AddRange(achievements.Progress(user)
            .Where(row => row.Type != LevelAchievementType)
            .Select(row => new AchievementDoingInfo(0, row.Type, row.SubType, row.Count)));
        return ProtocolResult.Success(new AchievementListResponse(infos));
    }
}

/// <summary>
/// Protocol 122 (parser case @0x15c1ae), sent at login right after 121: the collected rewards,
/// <c>achievement_reward_infos</c> read by <c>ParsingDataToAchieveFinish</c>@0x14c2e0 into the finish list (0x30 bytes
/// each). Unhandled, the bare reply left the list empty after every reload, so collected rules (Reach Lv. 5, 10) showed
/// again as in progress. reg_date must be non-empty for <c>CheckAchievementReward</c>@0xf6440.
/// </summary>
public sealed class AchievementRewardListHandler(
    PlayerDirectory directory, AchievementRepository achievements, TimeProvider clock) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        string now = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        List<AchievementRewardInfo> collected = achievements.Claimed(user)
            .Order()
            .Select(id => new AchievementRewardInfo(id, 0, now))
            .ToList();
        return ProtocolResult.Success(new AchievementRewardListResponse(collected));
    }
}

public sealed record AchievementDoingInfo(
    [property: JsonPropertyName("idx")] long Index,
    [property: JsonPropertyName("type")] long Type,
    [property: JsonPropertyName("sub_type")] long SubType,
    [property: JsonPropertyName("count")] long Count);

public sealed record AchievementListResponse(
    [property: JsonPropertyName("achievement_infos")] IReadOnlyList<AchievementDoingInfo> Infos);

public sealed record AchievementRewardListResponse(
    [property: JsonPropertyName("achievement_reward_infos")] IReadOnlyList<AchievementRewardInfo> Rewards);
