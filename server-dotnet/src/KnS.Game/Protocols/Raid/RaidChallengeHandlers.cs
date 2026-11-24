using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Rules;

namespace KnS.Game.Protocols.Raid;

/// <summary>Boss-specific challenge milestones, independent of weekly ranking.</summary>
public sealed class RaidChallengeService(RaidRepository raids)
{
    public IReadOnlyList<RaidChallengeInfo> Progress(long userNumber)
    {
        Dictionary<long, long> progress = raids.Challenges(userNumber).ToDictionary(row => row.RaidId, row => row.Count);
        foreach (RaidPlayRecord prior in raids.HistoricalScores(userNumber))
        {
            long stage = RaidRules.BossOf(prior.RaidNumber).StageId;
            long score = Math.Clamp(prior.Score, 0, int.MaxValue);
            if (score > progress.GetValueOrDefault(stage))
            {
                raids.RecordChallengeScore(userNumber, stage, score);
                progress[stage] = score;
            }
        }
        return progress.OrderBy(row => row.Key).Select(row => new RaidChallengeInfo(row.Key, row.Value)).ToList();
    }

    public IReadOnlyList<RaidChallengeClaimInfo> Claims(long userNumber) => raids.ChallengeClaims(userNumber)
        .Select(id => new RaidChallengeClaimInfo(id)).ToList();
}

public sealed record RaidChallengeInfo(
    [property: JsonPropertyName("raid_id")] long RaidId,
    [property: JsonPropertyName("count")] long Count);

public sealed record RaidChallengeClaimInfo([property: JsonPropertyName("challenge_id")] long ChallengeId);

public sealed class RaidChallengesHandler(PlayerDirectory directory, RaidChallengeService challenges) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request) => ProtocolResult.Success(
        new RaidChallengesResponse(challenges.Progress(directory.ResolveCurrent(request).UserNumber)));
}

public sealed record RaidChallengesResponse(
    [property: JsonPropertyName("raid_challenge_info")] IReadOnlyList<RaidChallengeInfo> Challenges);

public sealed class RaidChallengeClaimsHandler(PlayerDirectory directory, RaidChallengeService challenges) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request) => ProtocolResult.Success(
        new RaidChallengeClaimsResponse(challenges.Claims(directory.ResolveCurrent(request).UserNumber)));
}

public sealed record RaidChallengeClaimsResponse(
    [property: JsonPropertyName("raid_challenge_reward_info")] IReadOnlyList<RaidChallengeClaimInfo> Claims);

public sealed class RaidChallengeClaimHandler(
    PlayerDirectory directory, RaidRepository raids, PlayerProfileService profiles,
    GoodsRepository goods, FellowService fellows, RaidChallengeService challenges, PlayerRepository players,
    MailRepository mail, TimeProvider clock) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        long id = request.Parameters.GetInt64("challenge_id");
        RaidChallengeRule? rule = raids.ChallengeRules().FirstOrDefault(row => row.ChallengeId == id);
        if (rule is null || rule.RewardCount <= 0
            || (challenges.Progress(user).FirstOrDefault(row => row.RaidId == rule.RaidId)?.Count ?? 0) < rule.Threshold)
        {
            return ProtocolResult.Refused("raid challenge is not ready");
        }

        bool allyReward = rule.RewardItemId == 110004;
        if (!allyReward && rule.RewardItemId is not (CurrencyItems.Gold or CurrencyItems.Gems or 1900012 or 1900014 or CurrencyItems.HeroPoints))
        {
            return ProtocolResult.Refused("unsupported raid challenge reward");
        }
        if (!raids.ClaimChallenge(user, id)) return ProtocolResult.Refused("raid challenge already collected");

        IReadOnlyList<FellowInfo> granted = [];
        IReadOnlyList<FellowBookEntry> book = [];
        if (allyReward)
        {
            HashSet<long> numbers = [];
            numbers.Add(fellows.Grant(user, rule.RewardItemId));
            // Native 511 accepts one ally. Deliver overflow through the real mailbox,
            // whose 115 claim parser supports arrays, so no ally remains invisible in the client.
            if (rule.RewardCount > 1)
                mail.Add(user, rule.RewardItemId, rule.RewardCount - 1, message: "Additional Raid challenge allies",
                    regDate: clock.GetUtcNow().ToUnixTimeSeconds());
            granted = fellows.GetFellowInfos(user, numbers);
            book = fellows.GetBook(user, new HashSet<long> { rule.RewardItemId });
        }
        else if (rule.RewardItemId == CurrencyItems.Gold) profiles.AddCurrency(user, gold: rule.RewardCount);
        else if (rule.RewardItemId == CurrencyItems.Gems) profiles.AddCurrency(user, gems: rule.RewardCount);
        else if (rule.RewardItemId == CurrencyItems.Stamina)
        {
            (long stamina, long lastGain) = profiles.GetStamina(user);
            players.SetStamina(user, checked(stamina + rule.RewardCount), lastGain);
        }
        else goods.Add(user, rule.RewardItemId, rule.RewardCount);

        return ProtocolResult.Success(new RaidChallengeClaimResponse
        {
            Claim = new RaidChallengeClaimInfo(id), Goods = profiles.GetGoods(user),
            Fellow = granted.Count == 0 ? EmptyObject.Instance : granted[0],
            FellowBook = book.Count == 0 ? EmptyObject.Instance : book[0],
            GrantedFellows = granted,
        });
    }
}

public sealed record RaidChallengeClaimResponse
{
    [JsonPropertyName("raid_challenge_reward_info")] public required RaidChallengeClaimInfo Claim { get; init; }
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("fellow_info")] public object Fellow { get; init; } = EmptyObject.Instance;
    [JsonPropertyName("fellow_book_info")] public object FellowBook { get; init; } = EmptyObject.Instance;
    [JsonPropertyName("item_info")] public object Item { get; init; } = EmptyObject.Instance;
    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
    // Supplementary inspection field; native 511 consumes the single fellow_info above.
    [JsonPropertyName("granted_fellow_infos")] public IReadOnlyList<FellowInfo> GrantedFellows { get; init; } = [];
}
