using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Friends;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Protocols.Stages;
using KnS.Game.Stages;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Raid;

/// <summary>
/// The raid rules. One boss at a time, a new one each calendar week from the seven raid stages that have challenge data.
/// The week length and level are guesses. Raid fights are timed damage challenges rather than shared boss-health races.
/// </summary>
public static class RaidRules
{
    public const long WeekSeconds = 7 * 24 * 3600;
    public const long BossHealth = 2_000_000_000;

    /// <summary>A raid number uses week * 1000; the reserved round component remains zero.</summary>
    public const long RoundsPerWeek = 1000;
    public const long BossLevel = 10;
    // Native ready-screen guard: gameInfo+0x2934 = 10; text 9999012 says "anymore today".
    // UTC midnight follows the restored server's other daily systems; original timezone is unknown.
    public const long DailyEntryLimit = 10;

    /// <summary>
    /// The raid stages (stage table 1) and the boss file each fights. Only these seven have challenge (mission) rows,
    /// and their stage-row banner art matches their boss; 3100016/21/22 reuse another stage's banner and have no challenges.
    /// </summary>
    public static readonly IReadOnlyList<(long StageId, string MonsterFile)> Bosses =
    [
        (3100011, "dt_st_raid_spider"), (3100012, "dt_st_boss"), (3100013, "dt_st_raid_darkelf"),
        (3100014, "dt_st_raid_livingarmor"), (3100015, "dt_st_raid_mummy"), (3100019, "dt_st_raid_imp"),
        (3100020, "dt_st_raid_ghost"),
    ];

    public static long WeekOf(long unixSeconds) => unixSeconds / WeekSeconds;

    public static (long StageId, string MonsterFile) BossOf(long raidNumber) => Bosses[(int)((raidNumber / RoundsPerWeek + raidNumber % RoundsPerWeek) % Bosses.Count)];
}

/// <summary>A RaidDungeonInfo (ParsingDataToRaidDungeonInfo@0x14bb84).</summary>
public sealed record RaidDungeonInfo(
    [property: JsonPropertyName("raid_no")] long RaidNumber,
    [property: JsonPropertyName("stage_id")] long StageId,
    [property: JsonPropertyName("boss_id")] long BossId,
    [property: JsonPropertyName("remain_time")] long RemainTime,
    [property: JsonPropertyName("boss_hp")] long BossHealth);

/// <summary>A UserRaidInfo (ParsingDataToUserRaidInfo@0x14bab4).</summary>
public sealed record UserRaidInfo
{
    [JsonPropertyName("user_no")] public long UserNumber { get; init; }
    [JsonPropertyName("raid_no")] public long RaidNumber { get; init; }
    [JsonPropertyName("count")] public long Count { get; init; }
    [JsonPropertyName("score")] public long Score { get; init; }
    [JsonPropertyName("rank")] public long Rank { get; init; }
    [JsonPropertyName("current_score")] public long CurrentScore { get; init; }
    [JsonPropertyName("total_user_count")] public long TotalUsers { get; init; }
}

/// <summary>A raid ranking row: a friend-info plus the score.</summary>
public sealed record RaidRankEntry : FriendInfo
{
    [JsonPropertyName("score")] public long Score { get; init; }
    [JsonPropertyName("rank")] public long Rank { get; init; }
}

public sealed class RaidService(RaidRepository raids, FriendService friends, PlayerRepository players, TimeProvider clock)
{
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>The weekly raid remains the same regardless of player damage.</summary>
    public (long Number, long EndsAt) CurrentState()
    {
        long now = Now;
        RaidControlRecord? control = raids.GetControl();
        if (control is null)
            return (RaidRules.WeekOf(now) * RaidRules.RoundsPerWeek, (RaidRules.WeekOf(now) + 1) * RaidRules.WeekSeconds);
        raids.LockControl();
        control = raids.GetControl()!;
        if (control.EndsAt > now) return (control.RaidNumber, control.EndsAt);
        long rounds = (now - control.EndsAt) / RaidRules.WeekSeconds + 1;
        long number = checked(control.RaidNumber + rounds);
        long end = checked(control.EndsAt + rounds * RaidRules.WeekSeconds);
        raids.SetControl(number, end);
        return (number, end);
    }

    public long Current => CurrentState().Number;
    public long SecondsLeft => Math.Max(0, CurrentState().EndsAt - Now);

    public void SetRemaining(long seconds)
    {
        var state = CurrentState();
        raids.SetControl(state.Number, checked(Now + seconds));
    }

    public void Advance(long? stageId = null)
    {
        var state = CurrentState();
        long next = checked(state.Number + 1);
        if (stageId is not null)
        {
            if (!RaidRules.Bosses.Any(b => b.StageId == stageId)) throw new ArgumentOutOfRangeException(nameof(stageId));
            while (RaidRules.BossOf(next).StageId != stageId) next = checked(next + 1);
        }
        raids.SetControl(next, checked(Now + RaidRules.WeekSeconds));
    }

    public RaidDungeonInfo GetRaid()
    {
        var state = CurrentState();
        return new(state.Number, RaidRules.BossOf(state.Number).StageId, 1, Math.Max(0, state.EndsAt - Now), RaidRules.BossHealth);
    }

    public UserRaidInfo GetUserInfo(long userNumber, long score = 0, long? raidNumber = null)
    {
        long raid = raidNumber ?? Current;
        IReadOnlyList<RaidScoreRecord> ranking = raids.GetRanking(raid, 10_000);
        RaidScoreRecord mine = raids.Get(raid, userNumber);
        int index = ranking.ToList().FindIndex(r => r.UserNumber == userNumber);
        return new UserRaidInfo
        {
            UserNumber = userNumber,
            RaidNumber = raid,
            Count = raids.DailyEntries(userNumber, Now / 86400),
            Score = mine.Score,
            Rank = index < 0 ? 0 : index + 1,
            CurrentScore = score,
            TotalUsers = ranking.Count,
        };
    }

    public IReadOnlyList<RaidRankEntry> GetRanking(long viewer, int skip, int take) =>
        raids.GetRanking(Current, 10_000).Skip(skip).Take(take).Select((r, index) =>
        {
            FriendInfo info = friends.ToInfo(Player.From(players.FindByUserNumber(r.UserNumber)!), viewer);
            return new RaidRankEntry
            {
                UserNumber = info.UserNumber, HubUid = info.HubUid, HubId = info.HubId, Nickname = info.Nickname, Level = info.Level,
                HeroPcid = info.HeroPcid, HeroAttack = info.HeroAttack, HeroDefence = info.HeroDefence, HeroHp = info.HeroHp,
                LastLoginTime = info.LastLoginTime, Score = r.Score, Rank = (long)skip + index + 1,
            };
        }).ToList();

    public void RecordPlay(long raidNumber, long userNumber, long score) => raids.RecordPlay(raidNumber, userNumber, score);

    public bool BeginPlay(long userNumber, long raidNumber)
    {
        if (!raids.TryUseDailyEntry(userNumber, Now / 86400, RaidRules.DailyEntryLimit)) return false;
        raids.BeginPlay(userNumber, raidNumber);
        return true;
    }

    /// <summary>Settle the started fight once; repeated results retain the first accepted score and week.</summary>
    public UserRaidInfo? CompletePlay(long userNumber, long score)
    {
        if (raids.GetPlay(userNumber) is not { } play)
        {
            return null;
        }
        if (play.Complete == 0)
        {
            raids.RecordPlay(play.RaidNumber, userNumber, score);
            raids.RecordChallengeScore(userNumber, RaidRules.BossOf(play.RaidNumber).StageId, Math.Min(score, int.MaxValue));
            raids.CompletePlay(userNumber, score);
        }
        else
        {
            score = play.Score;
        }
        return GetUserInfo(userNumber, score, play.RaidNumber);
    }
}

/// <summary>Protocol 501 (parser case @0x15b280): the raid board, <c>{user_no}</c>.</summary>
public sealed class RaidStatusHandler(PlayerDirectory directory, RaidService raid, RaidRepository raids) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        long current = raid.Current;
        long? pending = raids.LatestUnclaimedRaid(user, current);
        return ProtocolResult.Success(new RaidStatusResponse
        {
            Ongoing = [raid.GetRaid()], RewardRaid = new RaidRewardInfo(pending ?? current, pending is null ? 1 : 0, 0),
        });
    }
}

public sealed record RaidRewardInfo(
    [property: JsonPropertyName("raid_no")] long RaidNumber,
    [property: JsonPropertyName("take_reward")] long TakeReward,
    [property: JsonPropertyName("waiting_time")] long WaitingTime);

public sealed record RaidStatusResponse
{
    [JsonPropertyName("ongoing_raid_info")] public IReadOnlyList<RaidDungeonInfo> Ongoing { get; init; } = [];
    [JsonPropertyName("waiting_raid_info")] public IReadOnlyList<RaidDungeonInfo> Waiting { get; init; } = [];
    [JsonPropertyName("reward_raid_info")] public RaidRewardInfo RewardRaid { get; init; } = new(0, 1, 0);
    [JsonPropertyName("raid_buff_count")] public long BuffCount { get; init; }
}

/// <summary>Protocol 503 (parser case @0x15b990): start the raid fight, <c>{raid_no}</c>. Like a stage start, with the boss's stats.</summary>
public sealed class RaidStartHandler(
    PlayerDirectory directory, PlayerProfileService profiles, MonsterInfoBuilder monsters, RaidService raid,
    DailyQuestRepository quests, TimeProvider clock, ILogger<RaidStartHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        Player player = directory.ResolveCurrent(request);
        long raidNumber = raid.Current;
        if (request.Parameters.GetInt64("raid_no", raidNumber) != raidNumber)
        {
            return ProtocolResult.Refused("raid has changed; refresh the raid board");
        }
        (long stageId, string file) = RaidRules.BossOf(raidNumber);
        IReadOnlyList<JsonObject> rows = monsters.BuildForFile(stageId, file, RaidRules.BossLevel);
        if (!raid.BeginPlay(player.UserNumber, raidNumber))
            return ProtocolResult.Refused("you can't enter anymore today");
        quests.Add(player.UserNumber, clock.GetUtcNow().ToUnixTimeSeconds() / 86400, 5300003, 1);
        logger.LogInformation("Raid start: stage {Stage} boss {File}", stageId, file);
        return ProtocolResult.Success(new RaidStartResponse
        {
            UserInfo = profiles.BuildUserInfo(player),
            Goods = profiles.GetGoods(player.UserNumber),
            UserRaid = raid.GetUserInfo(player.UserNumber, raidNumber: raidNumber),
            DailyQuest = Quests.DailyQuestInfo.Current(quests, clock, player.UserNumber, 5300003),
            MonsterInfo = new Dictionary<string, object>
            {
                [$"monster_info_{stageId}"] = rows,
            },
            Stage = new StageSetup
            {
                StageNumber = stageId,
                MonsterInfo = new Dictionary<string, object>
                {
                    [$"monster_info_{stageId}"] = rows,
                    [$"monster_info_{stageId + 1}"] = rows,
                },
            },
        });
    }
}

public sealed record RaidStartResponse
{
    [JsonPropertyName("daily_quest_info")] public required Quests.DailyQuestInfo DailyQuest { get; init; }
    [JsonPropertyName("user_info")] public required UserInfo UserInfo { get; init; }
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("user_raid_info")] public UserRaidInfo UserRaid { get; init; } = new();
    [JsonPropertyName("stage_info")] public required StageSetup Stage { get; init; }
    [JsonPropertyName("verify_info")] public VerifyInfo Verify { get; init; } = new();
    [JsonExtensionData] public Dictionary<string, object> MonsterInfo { get; init; } = [];
}

/// <summary>Protocol 504 (parser case @0x15c4ac): the fight's result. The client sends its score and a lot of combat stats; the score is kept.</summary>
public sealed class RaidResultHandler(PlayerDirectory directory, RaidService raid, RaidChallengeService challenges, ILogger<RaidResultHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long score = Math.Max(0, request.Parameters.GetInt64("score"));
        logger.LogInformation("Raid result: score {Score}", score);
        UserRaidInfo? result = raid.CompletePlay(userNumber, score);
        return result is null
            ? ProtocolResult.Refused("no raid fight was started")
            : ProtocolResult.Success(new RaidResultResponse { UserRaid = result, Challenges = challenges.Progress(userNumber) });
    }
}

public sealed record RaidResultResponse
{
    [JsonPropertyName("user_raid_info")] public UserRaidInfo UserRaid { get; init; } = new();
    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
    [JsonPropertyName("raid_buff_count")] public long BuffCount { get; init; }
    [JsonPropertyName("raid_challenge_info")] public IReadOnlyList<RaidChallengeInfo> Challenges { get; init; } = [];
    [JsonPropertyName("story_card_infos")] public IReadOnlyList<object> StoryCards { get; init; } = [];
}

/// <summary>Protocol 505 (parser case @0x15bfa8): the raid ranking, <c>{user_no, page, count}</c>.</summary>
public sealed class RaidRankingHandler(PlayerDirectory directory, RaidService raid) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long page = Math.Max(1, request.Parameters.GetInt64("page", 1));
        long count = Math.Clamp(request.Parameters.GetInt64("count", 50), 1, 100);
        return ProtocolResult.Success(new RaidRankingResponse
        {
            Ranking = raid.GetRanking(userNumber, (int)((page - 1) * count), (int)count),
            UserRanking = raid.GetUserInfo(userNumber),
        });
    }
}

public sealed record RaidRankingResponse
{
    [JsonPropertyName("raid_ranking_info")] public IReadOnlyList<RaidRankEntry> Ranking { get; init; } = [];
    [JsonPropertyName("user_raid_ranking_info")] public UserRaidInfo UserRanking { get; init; } = new();
    [JsonPropertyName("cooltime")] public long CoolTime { get; init; }
}

/// <summary>Protocols 506, 508, 510 and 511 (the raid reward tables and challenge counts): nothing is paid yet, so each answers an empty list.</summary>
public sealed class RaidRewardTableHandler : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request) => ProtocolResult.Success(new RaidRewardTableResponse());
}

public sealed record RaidRewardTableResponse
{
    [JsonPropertyName("reward_raid_info")] public JsonObject Reward { get; init; } = new();
    [JsonPropertyName("raid_challenge_reward_info")] public IReadOnlyList<object> ChallengeRewards { get; init; } = [];
    [JsonPropertyName("raid_challenge_info")] public IReadOnlyList<object> Challenges { get; init; } = [];
}
