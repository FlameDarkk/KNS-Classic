using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Rewards;
using KnS.Game.Rules;

namespace KnS.Game.Protocols.Achievements;

/// <summary>
/// Protocol 123: the "Collect" button of Achievements In progress. The request carries <c>achievement_id</c> (built at
/// @0x14f278). The reply parser (@0x15c248) reads <c>achievement_reward_info</c> as ONE object (achievement_id, idx, reg_date;
/// an array leaves id 0) into the finish list; CheckAchievementReward@0xf6440 treats a record with a non-empty reg_date as
/// collected. It also reads <c>update_achievement_info</c> and <c>update_item_etc_info</c>.
/// A repeat claim of a collected rule succeeds again without paying, so a client that missed the record can catch up.
/// A bare reply shows "Reward Collected" but leaves the rule unchanged, which is the bug this handler fixes.
/// Rules form chains (previous_id): a rule is collectable once, after its predecessor. "Reach Lv." rules (type 0) are checked
/// against the account level. Other rules are counted on the client, so the server cannot verify them and grants the
/// listed reward (restoration choice).
/// </summary>
public sealed class AchievementClaimHandler(
    PlayerDirectory directory, AchievementRepository achievements, PlayerRepository players,
    PlayerProfileService profiles, FellowService fellows, RewardGrantService rewards, TimeProvider clock) : IProtocolHandler
{
    private const long LevelRuleType = 0;

    public ProtocolResult Handle(GatewayRequest request)
    {
        Player player = directory.ResolveCurrent(request);
        long user = player.UserNumber;
        long achievementId = request.Parameters.GetInt64("achievement_id");
        AchievementRule? rule = achievements.Rules().FirstOrDefault(row => row.AchievementId == achievementId);
        if (rule is null)
        {
            return ProtocolResult.Refused("achievement is unavailable");
        }

        if (rule.RewardCount > 0 && rewards.Classify(rule.RewardItemId) == RewardKind.Unknown)
        {
            return ProtocolResult.Refused("achievement reward is not available");
        }

        if (rule.Type == LevelRuleType && (players.FindAccount(user)?.Level ?? 0) < rule.Goal)
        {
            return ProtocolResult.Refused("achievement is not ready to collect");
        }

        HashSet<long> claimed = achievements.Claimed(user);
        if (claimed.Contains(achievementId))
        {
            return Collected(user, achievementId);
        }

        if (rule.PreviousId != 0 && !claimed.Contains(rule.PreviousId))
        {
            return ProtocolResult.Refused("achievement is not ready to collect");
        }

        if (!achievements.TryClaim(user, achievementId))
        {
            return ProtocolResult.Refused("achievement is not ready to collect");
        }

        GrantedRewards granted = rewards.Grant(user, rule.RewardItemId, rule.RewardCount);
        return Collected(user, achievementId, granted.FellowNumbers);
    }

    /// <summary>The reply; a granted ally also goes in fellow_info / fellow_book_info, which the 123 parser reads.</summary>
    private ProtocolResult Collected(long user, long achievementId, IReadOnlyList<long>? allyNumbers = null)
    {
        IReadOnlyList<FellowInfo> newAllies = allyNumbers is { Count: > 0 } ? fellows.GetFellowInfos(user, allyNumbers.ToHashSet()) : [];
        IReadOnlyList<FellowBookEntry> book = newAllies.Count > 0 ? fellows.GetBook(user, newAllies.Select(f => f.FellowId).ToHashSet()) : [];
        return ProtocolResult.Success(new AchievementClaimResponse(
            new AchievementRewardInfo(achievementId, 0, clock.GetUtcNow().ToUnixTimeSeconds().ToString()),
            profiles.GetGoods(user))
        {
            Fellow = newAllies.Count > 0 ? newAllies[0] : null,
            FellowBook = book.Count > 0 ? book[0] : null,
        });
    }

}

public sealed record AchievementRewardInfo(
    [property: JsonPropertyName("achievement_id")] long AchievementId,
    [property: JsonPropertyName("idx")] long Index,
    [property: JsonPropertyName("reg_date")] string RegDate);

public sealed record AchievementClaimResponse(
    [property: JsonPropertyName("achievement_reward_info")] AchievementRewardInfo Reward,
    [property: JsonPropertyName("update_item_etc_info")] IReadOnlyList<GoodsEntry> Goods)
{
    [JsonPropertyName("fellow_info"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FellowInfo? Fellow { get; init; }
    [JsonPropertyName("fellow_book_info"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FellowBookEntry? FellowBook { get; init; }
}
