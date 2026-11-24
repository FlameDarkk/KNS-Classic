using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Gateway;
using KnS.Game.Players;
using KnS.Game.Rewards;

namespace KnS.Game.Protocols.Raid;

public static class RaidRankRewardRules
{
    /// <summary>Absolute top-100 ranks take precedence; later tiers follow the browser's displayed percentages.</summary>
    public static RaidRankRewardRule? Select(IReadOnlyList<RaidRankRewardRule> rules, long rank, long total)
    {
        if (rank < 1 || total < rank) return null;
        long percent = rank * 100 / total;
        return rules.FirstOrDefault(row => row.RankMax > 0
            ? rank >= row.RankMin && rank <= row.RankMax
            : rank > 100 && percent <= row.PercentMax);
    }
}

/// <summary>Protocol 506 queues the closed raid's rank reward in the message box, as its native success message promises.</summary>
public sealed class RaidRankRewardHandler(PlayerDirectory directory, RaidService raid, RaidRepository raids, MailRepository mail, TimeProvider clock) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        long number = request.Parameters.GetInt64("raid_no");
        if (number <= 0 || number >= raid.Current || raids.Get(number, user).Plays <= 0)
            return ProtocolResult.Refused("no completed raid participation");
        RaidRankSnapshot? standing = raids.FrozenRank(user, number);
        RaidRankRewardRule? reward = standing is null ? null : RaidRankRewardRules.Select(raids.RankRewards(), standing.Rank, standing.Total);
        if (reward is null) return ProtocolResult.Refused("raid standing unavailable");
        if (!raids.ClaimRank(user, number)) return ProtocolResult.Refused("raid rank reward already collected");
        mail.Add(user, reward.Reward1Item, reward.Reward1Value, reward.Reward2Item, reward.Reward2Value,
            message: $"Raid {number}: rank {standing!.Rank}", textCode: MailTexts.RaidReward, regDate: clock.GetUtcNow().ToUnixTimeSeconds());
        return ProtocolResult.Success(new RaidRankRewardResponse(new RaidRankRewardInfo(reward.Grade,
            reward.Reward1Item, reward.Reward1Value, reward.Reward2Item, reward.Reward2Value, reward.TextIndex)));
    }
}

public sealed record RaidRankRewardInfo(
    [property: JsonPropertyName("Grade")] long Grade,
    [property: JsonPropertyName("Reward1_Item")] long Reward1Item,
    [property: JsonPropertyName("Reward1_Value")] long Reward1Value,
    [property: JsonPropertyName("Reward2_Item")] long Reward2Item,
    [property: JsonPropertyName("Reward2_Value")] long Reward2Value,
    [property: JsonPropertyName("Text_Index")] long TextIndex);

public sealed record RaidRankRewardResponse([property: JsonPropertyName("reward_raid_info")] RaidRankRewardInfo Reward);
