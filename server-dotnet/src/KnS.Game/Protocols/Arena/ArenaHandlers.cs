using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Friends;
using KnS.Game.Gateway;
using KnS.Game.Inventory;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Rewards;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Arena;

/// <summary>
/// Arena rules. The values are dt_etc_setting's (Pvp_Point_Result1..7); what each result code means and how a season is cut are not known, so
/// results 1-3 count as wins. Seasons use a persisted seven-day schedule; rewards are not paid yet.
/// </summary>
public static class ArenaRules
{
    public static readonly long[] ResultPoints = [14, 9, 4, 3, 2, 1, 0];
    public const long WeekSeconds = 7 * 24 * 3600;
    public const long RefreshGems = 5;
    public const long RefreshCooldownSeconds = 1800;
    public const int ErrnoRefreshCooldown = 22007;
    public const long MaxStamina = 10;
    /// <summary>dt_etc_setting.json Init_Value "Pvp_MaxConsecutiveScore": max bonus points per consecutive win.</summary>
    public const long MaxConsecutiveScore = 10;
    /// <summary>Window (seconds) to keep an active win streak alive (buff_remain_time).</summary>
    public const long StreakDurationSeconds = 3600;
    /// <summary>dt_etc_setting.json PvpRecovery: 1st refill = 3 Gems, 2nd = 5 Gems, 3rd+ = 10 Gems per UTC day.</summary>
    public static long RecoveryGemsForCount(long usedToday) => usedToday switch { <= 0 => 3, 1 => 5, _ => 10 };
    /// <summary>Bonus points for a consecutive win: 0 on 1st win, +1 on 2nd, ..., capped at +10 (Pvp_MaxConsecutiveScore).</summary>
    public static long ConsecutiveBonus(long newStreak) => newStreak >= 2 ? Math.Min(newStreak - 1, MaxConsecutiveScore) : 0;
    public const long KnockOutResult = 1;
    public const long WinResult = 2;
    public const long LossResult = 6;
    /// <summary>
    /// Give up ("recorded as a loss"): Pvp_Point_Result7 = 0 points, counted as a battle, streak reset. The code is a restoration choice; the
    /// opponent row (DrawBattleRanking) shows "Wins" only for results 1-2, so the Battle button stays.
    /// </summary>
    public const long GiveUpResult = 7;

    public static long PointsFor(long result) => result is >= 1 and <= 7 ? ResultPoints[result - 1] : 0;

    public static bool IsWin(long result) => result is >= 1 and <= 3;

    /// <summary>The results DrawBattleRanking marks "Wins" and the result scene counts as beaten (1 and 2).</summary>
    public static bool ShowsWin(long result) => result is 1 or 2;

    public static long WeekOf(long unixSeconds) => unixSeconds / WeekSeconds;

    /// <summary>
    /// Grades 1-8 are the absolute ranks the Reward tab labels (1st, 2nd, 3rd, 4th-10th, 11th-20th, 21st-30th, 31st-50th, 51st-100th);
    /// grades 9-20 are percentage bands for rank 101 and below (0~1%, 2~5%, 6~10%, 11~20% ... 91~100%), the same layout as the
    /// raid rank table. The percentage is rounded up, so rank 101 of 10,000 (1.01%) falls in 2~5%.
    /// </summary>
    private static readonly long[] RankMax = [1, 2, 3, 10, 20, 30, 50, 100];
    private static readonly long[] PercentMax = [1, 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

    /// <summary>
    /// The 7 native Altar -> Arena limited ally pairs (master_altar_recipes rows 15-21, in Altar order).
    /// Native loading tips (5400014, 5400022) and DrawRankingReward@0xdf120 confirm Com2uS rotated Arena-exclusive
    /// allies per season through Protocol 405; the rotation order and 6★ (1st-3rd) / 5★ (4th-100th) split are restoration choices.
    /// </summary>
    public static readonly IReadOnlyList<ArenaLimitedAllyPair> LimitedAllyPairs =
    [
        new(100498, 100499, "Battle Flower Caroline", "Battle Queen Caroline"),
        new(100501, 100502, "Roaming Gladiator Shuske", "Lunar Gladiator Shuske"),
        new(100511, 100512, "Succubus Lyrith", "Succubus Queen Lyrith"),
        new(100519, 100520, "Assassin Hatsan", "Deadly Shadow Hatsan"),
        new(100521, 100522, "Deadly Gaze Medusa", "Destructive Bride Medusa"),
        new(105014, 105015, "Valkyri Brynhild", "Mystical Light Brynhild"),
        new(105016, 105017, "Paladin Leon", "Holy Paladin Leon"),
    ];

    public static ArenaLimitedAllyPair? FindLimitedAlly(long allyId) =>
        LimitedAllyPairs.FirstOrDefault(pair => pair.SixStarId == allyId || pair.FiveStarId == allyId);

    public static ArenaLimitedAllyPair FeaturedAllyFor(long seasonNumber, long featuredAllyOverride = 0)
    {
        if (featuredAllyOverride > 0 && FindLimitedAlly(featuredAllyOverride) is { } pinned)
        {
            return pinned;
        }
        long index = (Math.Max(1, seasonNumber) - 1) % LimitedAllyPairs.Count;
        return LimitedAllyPairs[(int)index];
    }

    public static IReadOnlyList<ArenaRankingReward> RankingRewardsFor(ArenaLimitedAllyPair pair) =>
        RankingRewards.Select(row => row.Grade switch
        {
            >= 1 and <= 3 => row with { Reward1Item = pair.SixStarId, Reward1Value = 1 },
            >= 4 and <= 8 => row with { Reward1Item = pair.FiveStarId, Reward1Value = 1 },
            _ => row,
        }).ToList();

    public static ArenaRankingReward? RankingRewardFor(long rank, long total, ArenaLimitedAllyPair? pair = null)
    {
        if (rank < 1 || total < rank) return null;
        int index = Array.FindIndex(RankMax, max => rank <= max);
        if (index < 0)
        {
            long percent = (rank * 100 + total - 1) / total;
            index = RankMax.Length + Array.FindIndex(PercentMax, max => percent <= max);
        }
        IReadOnlyList<ArenaRankingReward> table = pair is null ? RankingRewards : RankingRewardsFor(pair);
        return table.FirstOrDefault(row => row.Grade == index + 1);
    }

    // Native 1.4.0 season ranking rewards (tickets + gems).
    public static readonly IReadOnlyList<ArenaRankingReward> RankingRewards =
    [
        new() { Grade = 1, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 500, TextIndex = 9993501 },
        new() { Grade = 2, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 400, TextIndex = 9993502 },
        new() { Grade = 3, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 300, TextIndex = 9993503 },
        new() { Grade = 4, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 200, TextIndex = 9993504 },
        new() { Grade = 5, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 150, TextIndex = 9993505 },
        new() { Grade = 6, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 120, TextIndex = 9993506 },
        new() { Grade = 7, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 100, TextIndex = 9993507 },
        new() { Grade = 8, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 90, TextIndex = 9993508 },
        new() { Grade = 9, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 70, TextIndex = 9993509 },
        new() { Grade = 10, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 60, TextIndex = 9993510 },
        new() { Grade = 11, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 50, TextIndex = 9993511 },
        new() { Grade = 12, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 40, TextIndex = 9993512 },
        new() { Grade = 13, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 30, TextIndex = 9993513 },
        new() { Grade = 14, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 20, TextIndex = 9993514 },
        new() { Grade = 15, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 10, TextIndex = 9993515 },
        new() { Grade = 16, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 8, TextIndex = 9993516 },
        new() { Grade = 17, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 6, TextIndex = 9993517 },
        new() { Grade = 18, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 5, TextIndex = 9993518 },
        new() { Grade = 19, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 3, TextIndex = 9993519 },
        new() { Grade = 20, Reward1Item = 1900012, Reward1Value = 1, Reward2Item = 1900009, Reward2Value = 1, TextIndex = 9993520 },
    ];

    public static readonly IReadOnlyList<ArenaMissionReward> MissionRewards =
    [
        // Native 1.4.0 Arena score mission rewards.
        new() { Grade = 1, Score = 50, RewardItem = 1900014, RewardValue = 20 },
        new() { Grade = 2, Score = 100, RewardItem = 1902002, RewardValue = 5 },
        new() { Grade = 3, Score = 150, RewardItem = 1900008, RewardValue = 10000 },
        new() { Grade = 4, Score = 200, RewardItem = 1600002, RewardValue = 3 },
        new() { Grade = 5, Score = 250, RewardItem = 1902001, RewardValue = 5 },
        new() { Grade = 6, Score = 300, RewardItem = 1900009, RewardValue = 6 },
        new() { Grade = 7, Score = 350, RewardItem = 110002, RewardValue = 1 },
        new() { Grade = 8, Score = 400, RewardItem = 1900014, RewardValue = 30 },
        new() { Grade = 9, Score = 450, RewardItem = 1902007, RewardValue = 5 },
        new() { Grade = 10, Score = 500, RewardItem = 1900008, RewardValue = 20000 },
        new() { Grade = 11, Score = 550, RewardItem = 1600002, RewardValue = 5 },
        new() { Grade = 12, Score = 600, RewardItem = 1902003, RewardValue = 5 },
        new() { Grade = 13, Score = 650, RewardItem = 1900009, RewardValue = 10 },
        new() { Grade = 14, Score = 700, RewardItem = 110003, RewardValue = 1 },
        new() { Grade = 15, Score = 750, RewardItem = 1900014, RewardValue = 40 },
        new() { Grade = 16, Score = 800, RewardItem = 1902008, RewardValue = 5 },
        new() { Grade = 17, Score = 850, RewardItem = 1900008, RewardValue = 30000 },
        new() { Grade = 18, Score = 900, RewardItem = 1600002, RewardValue = 5 },
        new() { Grade = 19, Score = 950, RewardItem = 1902005, RewardValue = 5 },
        new() { Grade = 20, Score = 1000, RewardItem = 1900009, RewardValue = 20 },
        new() { Grade = 21, Score = 1050, RewardItem = 110004, RewardValue = 1 },
        new() { Grade = 22, Score = 1100, RewardItem = 1900014, RewardValue = 50 },
        new() { Grade = 23, Score = 1150, RewardItem = 1902006, RewardValue = 5 },
        new() { Grade = 24, Score = 1200, RewardItem = 1900008, RewardValue = 40000 },
        new() { Grade = 25, Score = 1250, RewardItem = 1600002, RewardValue = 10 },
        new() { Grade = 26, Score = 1300, RewardItem = 1902004, RewardValue = 5 },
        new() { Grade = 27, Score = 1350, RewardItem = 1900009, RewardValue = 30 },
        new() { Grade = 28, Score = 1400, RewardItem = 1900019, RewardValue = 1 },
    ];
}

/// <summary>An opponent or ranking row: a friend-info plus the arena columns.</summary>
public sealed record PvpEntry : FriendInfo
{
    [JsonPropertyName("score")] public long Score { get; init; }
    [JsonPropertyName("pvp_result")] public long Result { get; init; }
    [JsonPropertyName("rank")] public long Rank { get; init; }
}

/// <summary>user_pvp_ranking_info (ParsingDataToUserPvpInfo@0x14b104).</summary>
public sealed record UserPvpInfo
{
    [JsonPropertyName("total_pvp_count")] public long TotalBattles { get; init; }
    [JsonPropertyName("total_user_cnt")] public long TotalUsers { get; init; }
    [JsonPropertyName("total_win_count")] public long TotalWins { get; init; }
    [JsonPropertyName("user_pvp_point")] public long Points { get; init; }
    [JsonPropertyName("consecutive_win_count")] public long Streak { get; init; }
    [JsonPropertyName("buff_remain_time")] public long BuffRemainTime { get; init; }
    [JsonPropertyName("rank")] public long Rank { get; init; }
    [JsonPropertyName("last_grade")] public long LastGrade { get; init; }
}

/// <summary>Reads and writes the arena standings; opponents are the other real players.</summary>
public sealed class ArenaService(PvpRepository pvp, FriendService friends, PlayerRepository players, GoodsRepository goods, MailRepository mail, TimeProvider clock)
{
    /// <summary>The Battle Spirit item (master_etc_item 1900017): one is used per battle, at most <see cref="ArenaRules.MaxStamina"/>.</summary>
    public const long BattleSpiritItem = 1900017;

    /// <summary>A player's first visit fills the Battle Spirits.</summary>
    public void EnsureSpirit(long userNumber)
    {
        if (goods.GetAll(userNumber).All(row => row.ItemId != BattleSpiritItem))
        {
            goods.Add(userNumber, BattleSpiritItem, ArenaRules.MaxStamina);
        }
        goods.ArenaSpiritClock(userNumber, Now);
    }

    public void UseSpirit(long userNumber)
    {
        EnsureSpirit(userNumber);
        if (goods.GetAll(userNumber).First(row => row.ItemId == BattleSpiritItem).Count > 0)
        {
            goods.Add(userNumber, BattleSpiritItem, -1);
        }
    }

    public bool BeginBattle(long userNumber, long opponentNumber)
    {
        PvpControlRecord season = CurrentSeason();
        EnsureSpirit(userNumber);
        if (season.Closed != 0 || userNumber == opponentNumber || Find(opponentNumber) is null
            || goods.GetAll(userNumber).First(row => row.ItemId == BattleSpiritItem).Count <= 0)
        {
            return false;
        }
        pvp.BeginPlay(userNumber, opponentNumber);
        pvp.CaptureSeason(userNumber, season.Generation);
        UseSpirit(userNumber);
        return true;
    }

    /// <summary>Complete a matching battle once. Entry spends a spirit; a give-up is recorded as a 0-point loss.</summary>
    public long? CompleteBattle(long userNumber, long opponentNumber, long result, bool giveUp)
    {
        if (pvp.GetPlay(userNumber) is not { } play || play.OpponentNumber != opponentNumber)
        {
            return null;
        }
        if (play.Complete != 0)
        {
            return play.Result;
        }
        if (giveUp) result = ArenaRules.GiveUpResult;
        PvpControlRecord season = CurrentSeason();
        long generation = pvp.AttemptSeason(userNumber) ?? season.Generation;
        // Attempts started before this migration have not paid their entry yet.
        if (pvp.AttemptSeason(userNumber) is null) UseSpirit(userNumber);
        if (generation == season.Generation)
        {
            RecordResult(userNumber, result);
            pvp.SetOpponentResult(userNumber, opponentNumber, result);
        }
        else pvp.RecordHistoricalBattle(generation, userNumber, ArenaRules.PointsFor(result), ArenaRules.IsWin(result));
        pvp.CompletePlay(userNumber, result);
        return result;
    }

    /// <summary>
    /// Pays every Arena score mission (pvp_mission_reward_info) the season's score has reached and not yet paid, into the Inbox.
    /// Returns the grades paid now; 403 lists them in <c>grade</c> with <c>is_reward = 1</c> so the client shows its Mission Complete box.
    /// Inbox delivery is a restoration choice following the native "PvP Reward has been sent to your Inbox" flow.
    /// </summary>
    public IReadOnlyList<long> SettleMissions(long userNumber)
    {
        PvpControlRecord season = CurrentSeason();
        long points = pvp.Get(userNumber).Points;
        var paid = new List<long>();
        foreach (ArenaMissionReward reward in ArenaRules.MissionRewards)
        {
            if (reward.Score > points || !pvp.TryMarkMission(season.Generation, userNumber, reward.Grade)) continue;
            mail.Add(userNumber, reward.RewardItem, reward.RewardValue, message: $"Arena Mission: {reward.Score} pts",
                textCode: MailTexts.PvpMissionReward, regDate: Now);
            paid.Add(reward.Grade);
        }
        return paid;
    }

    /// <summary>
    /// Season end: mails every player with at least 1 score this season the Reward-tab row for their final rank (native mail title 9800021
    /// "PvP Reward"), once per season. Rank and total count only players with at least 1 score this season. Restoration choice: the original
    /// server's cut-off and eligibility are unknown; the client only shows the table (405) and parses one claimed row (404).
    /// </summary>
    private void SettleRanking(PvpControlRecord season)
    {
        ArenaLimitedAllyPair featured = FeaturedAlly(season);
        IReadOnlyList<PvpStatRecord> standings = pvp.Participants();
        for (int i = 0; i < standings.Count; i++)
        {
            long rank = i + 1, user = standings[i].UserNumber;
            if (ArenaRules.RankingRewardFor(rank, standings.Count, featured) is not { } reward
                || !pvp.TryMarkRankingPaid(season.Generation, user, reward.Grade, rank, standings.Count, reward.Reward1Item, reward.Reward1Value)) continue;
            mail.Add(user, reward.Reward1Item, reward.Reward1Value, reward.Reward2Item, reward.Reward2Value,
                message: $"Arena Season {season.SeasonNumber}: rank {rank}", textCode: MailTexts.PvpReward, regDate: Now);
        }
    }

    public static ArenaLimitedAllyPair FeaturedAlly(PvpControlRecord season) =>
        ArenaRules.FeaturedAllyFor(season.SeasonNumber, season.FeaturedAlly);

    public static IReadOnlyList<ArenaRankingReward> RankingRewards(PvpControlRecord season) =>
        ArenaRules.RankingRewardsFor(FeaturedAlly(season));

    /// <summary>The Reward-tab row last mailed to this player, preserving the season's featured ally for 404.</summary>
    public ArenaRankingReward? LastRankingReward(long userNumber)
    {
        if (pvp.LastRankingPaid(userNumber) is not { } paid) return null;
        if (ArenaRules.RankingRewards.FirstOrDefault(row => row.Grade == paid.Grade) is not { } row) return null;
        return paid.Reward1Item > 0
            ? row with { Reward1Item = paid.Reward1Item, Reward1Value = paid.Reward1Value > 0 ? paid.Reward1Value : row.Reward1Value }
            : row;
    }

    public void FillSpirit(long userNumber)
    {
        EnsureSpirit(userNumber);
        long have = goods.GetAll(userNumber).First(row => row.ItemId == BattleSpiritItem).Count;
        goods.Add(userNumber, BattleSpiritItem, Math.Max(0, ArenaRules.MaxStamina - have));
    }

    public bool NeedsSpirit(long userNumber)
    {
        EnsureSpirit(userNumber);
        return goods.GetAll(userNumber).First(row => row.ItemId == BattleSpiritItem).Count < ArenaRules.MaxStamina;
    }

    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    public PvpControlRecord CurrentSeason()
    {
        long now = Now;
        PvpControlRecord state = pvp.Control(now, ArenaRules.WeekSeconds);
        if (state.Closed != 0 || state.EndsAt > now) return state;
        long rounds = (now - state.EndsAt) / ArenaRules.WeekSeconds + 1;
        SettleRanking(state);
        pvp.Archive(state.Generation);
        state = state with { Generation = checked(state.Generation + rounds), SeasonNumber = checked(state.SeasonNumber + rounds), EndsAt = checked(state.EndsAt + rounds * ArenaRules.WeekSeconds), FeaturedAlly = 0 };
        pvp.SetControl(state);
        return state;
    }

    public long Week => CurrentSeason().SeasonNumber;
    public long SecondsRemaining(PvpControlRecord state) => Math.Max(0, state.EndsAt - Now);
    public long SecondsLeftInWeek => SecondsRemaining(CurrentSeason());

    public void SetRemaining(long seconds)
    {
        PvpControlRecord state = CurrentSeason();
        pvp.SetControl(state with { EndsAt = checked(Now + seconds), Closed = 0 });
    }

    public void SetSeasonNumber(long number) => pvp.SetControl(CurrentSeason() with { SeasonNumber = number });

    public void SetFeaturedAlly(long featuredAllyId) => pvp.SetControl(CurrentSeason() with { FeaturedAlly = featuredAllyId });

    public void EndSeason() => pvp.SetControl(CurrentSeason() with { EndsAt = Now, Closed = 1 });

    public void NextSeason()
    {
        PvpControlRecord state = CurrentSeason();
        SettleRanking(state);
        pvp.Archive(state.Generation);
        pvp.SetControl(state with { Generation = checked(state.Generation + 1), SeasonNumber = checked(state.SeasonNumber + 1), EndsAt = checked(Now + ArenaRules.WeekSeconds), Closed = 0, FeaturedAlly = 0 });
    }

    /// <summary>The arena rank for a profile card's pvp_rank; 0 ("No Records") before the first battle this season.</summary>
    public long ProfileRank(long userNumber) => pvp.Get(userNumber).Battles > 0 ? GetInfo(userNumber).Rank : 0;

    public long NextRecoveryGems(long userNumber)
    {
        CurrentSeason();
        PvpStatRecord stat = pvp.Get(userNumber);
        long today = Now / 86400;
        long used = stat.RecoveryDay == today ? stat.RecoveryCount : 0;
        return ArenaRules.RecoveryGemsForCount(used);
    }

    public void RecordRecovery(long userNumber) => pvp.RecordRecovery(userNumber, Now / 86400);

    public UserPvpInfo GetInfo(long userNumber)
    {
        CurrentSeason();
        EnsureSpirit(userNumber);
        IReadOnlyList<PvpStatRecord> ranking = pvp.GetRanking(10_000);
        PvpStatRecord stat = pvp.Get(userNumber);
        int index = ranking.ToList().FindIndex(r => r.UserNumber == userNumber);
        long now = Now;
        bool expired = stat.Streak > 0 && stat.StreakUntil > 0 && now > stat.StreakUntil;
        long streak = expired ? 0 : stat.Streak;
        long buffRemain = streak > 0 && stat.StreakUntil > now ? stat.StreakUntil - now : 0;
        return new UserPvpInfo
        {
            TotalBattles = stat.Battles,
            TotalUsers = ranking.Count,
            TotalWins = stat.Wins,
            Points = stat.Points,
            Streak = streak,
            BuffRemainTime = buffRemain,
            Rank = index < 0 ? 0 : index + 1,
        };
    }

    /// <summary>
    /// The listed opponents. The list is stored and stays the same between battles; it is picked again only on a refresh, when every listed
    /// opponent is beaten (see <see cref="RotateIfAllBeaten"/>), or when listed players are gone.
    /// </summary>
    public IReadOnlyList<PvpEntry> GetOpponents(long userNumber, int limit)
    {
        CurrentSeason();
        IReadOnlyList<long> listed = pvp.OpponentList(userNumber).Where(no => no != userNumber && Find(no) is not null).ToList();
        if (listed.Count == 0)
        {
            listed = PickOpponents(userNumber, limit, []);
            pvp.SetOpponentList(userNumber, listed);
        }
        Dictionary<long, PvpStatRecord> stats = pvp.GetRanking(10_000).ToDictionary(r => r.UserNumber);
        return listed
            .Select(no => Entry(userNumber, stats.GetValueOrDefault(no) ?? new PvpStatRecord { UserNumber = no }, 0)
                with { Result = pvp.OpponentResult(userNumber, no) })
            .ToList();
    }

    /// <summary>
    /// Leaving the battle result scene (Scene_PointerReleaseEvent_SC_BATTLE_RESULT @0x4f2b0) walks the opponent list and, when every row's
    /// pvp_result is 1 or 2 (the rows that show "Wins"), drops the list and sends a plain 408 (refresh 0, use_cash 0). The server answers
    /// with a new list; the streak and the free-refresh timer are untouched. True when the list was rotated.
    /// </summary>
    public bool RotateIfAllBeaten(long userNumber)
    {
        CurrentSeason();
        IReadOnlyList<long> listed = pvp.OpponentList(userNumber);
        if (listed.Count == 0 || !listed.All(no => ArenaRules.ShowsWin(pvp.OpponentResult(userNumber, no))))
        {
            return false;
        }
        Rotate(userNumber, 5);
        return true;
    }

    /// <summary>
    /// New opponents: players not on the previous list come first (previous ones only fill a short server), nearest in score to the player;
    /// five are drawn at random from the nearest fifteen and shown best score first. The client sends no matching rule and the original
    /// server's is unknown, so the score window is a restoration choice.
    /// </summary>
    private List<long> PickOpponents(long userNumber, int limit, IReadOnlyCollection<long> previous)
    {
        long mine = pvp.Get(userNumber).Points;
        List<PvpStatRecord> others = pvp.GetRanking(10_000).Where(r => r.UserNumber != userNumber).ToList();
        IEnumerable<PvpStatRecord> Nearest(IEnumerable<PvpStatRecord> rows) =>
            rows.Select(r => (r, tie: Random.Shared.Next())).OrderBy(x => Math.Abs(x.r.Points - mine)).ThenBy(x => x.tie).Select(x => x.r);

        List<PvpStatRecord> fresh = Nearest(others.Where(r => !previous.Contains(r.UserNumber))).Take(limit * 3).ToList();
        List<PvpStatRecord> chosen = fresh.OrderBy(_ => Random.Shared.Next()).Take(limit).ToList();
        if (chosen.Count < limit)
        {
            chosen.AddRange(Nearest(others.Where(r => previous.Contains(r.UserNumber))).Take(limit - chosen.Count));
        }
        return chosen.OrderByDescending(r => r.Points).ThenBy(r => r.UserNumber).Select(r => r.UserNumber).ToList();
    }

    private void Rotate(long userNumber, int limit)
    {
        IReadOnlyList<long> previous = pvp.OpponentList(userNumber);
        pvp.ClearOpponentResults(userNumber);
        pvp.SetOpponentList(userNumber, PickOpponents(userNumber, limit, previous.ToHashSet()));
    }

    public long GetRemainRefreshTime(long userNumber)
    {
        CurrentSeason();
        return Math.Max(0, pvp.Get(userNumber).RefreshUntil - Now);
    }

    public void RefreshOpponents(long userNumber, bool resetStreak)
    {
        CurrentSeason();
        pvp.ApplyRefresh(userNumber, Now + ArenaRules.RefreshCooldownSeconds, resetStreak);
        Rotate(userNumber, 5);
    }

    public IReadOnlyList<PvpEntry> GetRanking(long viewer, int skip, int take)
    {
        CurrentSeason();
        IReadOnlyList<PvpStatRecord> ranking = pvp.GetRanking(10_000);
        return ranking.Skip(skip).Take(take).Select((r, i) => Entry(viewer, r, skip + i + 1)).ToList();
    }

    public void RecordResult(long userNumber, long result)
    {
        long now = Now;
        bool won = ArenaRules.IsWin(result);
        PvpStatRecord stat = pvp.Get(userNumber);
        long currentStreak = stat.Streak > 0 && (stat.StreakUntil == 0 || now <= stat.StreakUntil) ? stat.Streak : 0;
        long nextStreak = won ? currentStreak + 1 : 0;
        long streakUntil = won ? now + ArenaRules.StreakDurationSeconds : 0;
        long points = ArenaRules.PointsFor(result) + (won ? ArenaRules.ConsecutiveBonus(nextStreak) : 0);
        pvp.RecordBattle(userNumber, points, won, nextStreak, streakUntil);
    }

    public Player? Find(long userNumber) => players.FindByUserNumber(userNumber) is { } record ? Player.From(record) : null;

    private PvpEntry Entry(long viewer, PvpStatRecord stat, long rank)
    {
        FriendInfo info = friends.ToInfo(Find(stat.UserNumber)!, viewer);
        return new PvpEntry
        {
            UserNumber = info.UserNumber,
            HubUid = info.HubUid,
            HubId = info.HubId,
            Nickname = info.Nickname,
            Level = info.Level,
            LastLoginTime = info.LastLoginTime,
            HeroPcid = info.HeroPcid,
            HeroItemId = info.HeroItemId,
            HeroAttack = info.HeroAttack,
            HeroDefence = info.HeroDefence,
            HeroHp = info.HeroHp,
            Score = stat.Points,
            Rank = rank,
        };
    }
}

public sealed record ArenaLimitedAllyPair(long FiveStarId, long SixStarId, string FiveStarName, string SixStarName);

public sealed record ArenaRankingReward
{
    [JsonPropertyName("Grade")] public long Grade { get; init; }
    [JsonPropertyName("Reward1_Item")] public long Reward1Item { get; init; }
    [JsonPropertyName("Reward1_Value")] public long Reward1Value { get; init; }
    [JsonPropertyName("Reward2_Item")] public long Reward2Item { get; init; }
    [JsonPropertyName("Reward2_Value")] public long Reward2Value { get; init; }
    [JsonPropertyName("Text_Index")] public long TextIndex { get; init; }
}

public sealed record ArenaMissionReward
{
    [JsonPropertyName("Grade")] public long Grade { get; init; }
    [JsonPropertyName("Score")] public long Score { get; init; }
    [JsonPropertyName("Reward_Item")] public long RewardItem { get; init; }
    [JsonPropertyName("Reward_Value")] public long RewardValue { get; init; }
}

/// <summary>Protocol 405 (parser case @0x15eca0): the season and its reward lists, no parameters.</summary>
public sealed class ArenaSeasonHandler(ArenaService arena) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        PvpControlRecord season = arena.CurrentSeason();
        long remaining = arena.SecondsRemaining(season);
        return ProtocolResult.Success(new ArenaSeasonResponse
        {
            Week = season.SeasonNumber,
            CurrentRemain = remaining,
            NextRemain = remaining + ArenaRules.WeekSeconds,
            RankingRewards = ArenaService.RankingRewards(season),
            MissionRewards = ArenaRules.MissionRewards,
        });
    }
}

public sealed record ArenaSeasonResponse
{
    [JsonPropertyName("week")] public long Week { get; init; }
    [JsonPropertyName("cur_pvp_remain_time")] public long CurrentRemain { get; init; }
    [JsonPropertyName("next_pvp_remain_time")] public long NextRemain { get; init; }
    [JsonPropertyName("pvp_reward")] public long Reward { get; init; }
    [JsonPropertyName("pvp_ranking_reward_info")] public IReadOnlyList<ArenaRankingReward> RankingRewards { get; init; } = [];
    [JsonPropertyName("pvp_mission_reward_info")] public IReadOnlyList<ArenaMissionReward> MissionRewards { get; init; } = [];
    [JsonPropertyName("last_week_ranking")] public long LastWeekRanking { get; init; }
}

/// <summary>Protocol 408 (parser case @0x15c08a): the opponent list on Battle Opponent, <c>{refresh, use_cash, achievement_list}</c>.</summary>
public sealed class ArenaOpponentsHandler(PlayerDirectory directory, ArenaService arena, PlayerProfileService profiles) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        arena.EnsureSpirit(userNumber);
        bool refresh = request.Parameters.GetInt64("refresh") != 0;
        bool useCash = request.Parameters.GetInt64("use_cash") != 0;
        if (refresh)
        {
            if (useCash)
            {
                if (!profiles.TrySpend(userNumber, Rules.CurrencyItems.Gems, ArenaRules.RefreshGems))
                {
                    return ProtocolResult.Refused("not enough gems");
                }
                arena.RefreshOpponents(userNumber, resetStreak: false);
            }
            else
            {
                long remain = arena.GetRemainRefreshTime(userNumber);
                if (remain > 0)
                {
                    return ProtocolResult.Refused(
                        "refresh is on cooldown",
                        ArenaRules.ErrnoRefreshCooldown,
                        new { remain_refresh_time = remain });
                }
                arena.RefreshOpponents(userNumber, resetStreak: true);
            }
        }
        else
        {
            arena.RotateIfAllBeaten(userNumber);
        }
        // Scores reached before missions were paid: deliver them to the Inbox now.
        arena.SettleMissions(userNumber);
        return ProtocolResult.Success(new ArenaOpponentsResponse
        {
            Opponents = arena.GetOpponents(userNumber, 5),
            Info = arena.GetInfo(userNumber),
            Goods = profiles.GetGoods(userNumber),
            RemainRefreshTime = arena.GetRemainRefreshTime(userNumber),
            Recovery = new PvpRecovery(arena.NextRecoveryGems(userNumber)),
        });
    }
}

public sealed record PvpRecovery([property: JsonPropertyName("PvpRecoveryValue")] long Value);

public sealed record ArenaOpponentsResponse
{
    [JsonPropertyName("pvp_user_info")] public IReadOnlyList<PvpEntry> Opponents { get; init; } = [];
    [JsonPropertyName("user_pvp_ranking_info")] public UserPvpInfo Info { get; init; } = new();
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("remain_refresh_time")] public long RemainRefreshTime { get; init; }
    [JsonPropertyName("master_pvp_recovery_info")] public PvpRecovery Recovery { get; init; } = new(0);
}

/// <summary>Protocol 406 (parser case @0x15ebd0): the ranking, <c>{user_no, week_no, page, count}</c>.</summary>
public sealed class ArenaRankingHandler(PlayerDirectory directory, ArenaService arena) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long page = Math.Max(1, request.Parameters.GetInt64("page", 1));
        long count = Math.Clamp(request.Parameters.GetInt64("count", 50), 1, 100);
        return ProtocolResult.Success(new ArenaRankingResponse(arena.GetRanking(userNumber, (int)((page - 1) * count), (int)count), 0));
    }
}

public sealed record ArenaRankingResponse(
    [property: JsonPropertyName("pvp_ranking_info")] IReadOnlyList<PvpEntry> Ranking,
    [property: JsonPropertyName("cooltime")] long CoolTime);

/// <summary>
/// Protocol 402 (parser case @0x15cbf0): the snapshot of the opponent to fight, <c>{user_no, pvp_user_no, week_no}</c>. The client fights it
/// by itself and reports the outcome in 403.
/// </summary>
public sealed class ArenaBattleStartHandler(
    ArenaService arena,
    FriendService friends,
    PlayerProfileService profiles,
    InventoryService inventory,
    FellowService fellows,
    FellowItemService fellowItems,
    DailyQuestRepository quests,
    TimeProvider clock,
    ILogger<ArenaBattleStartHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long opponentNumber = request.Parameters.GetInt64("pvp_user_no");
        if (arena.Find(opponentNumber) is not { } opponent)
        {
            return ProtocolResult.Refused("no such player");
        }

        // Your own card opens the same comparison popup (allies, equipment). It is a view only: no entry, spirit, daily quest or standing change.
        if (opponentNumber != request.HeaderUserNumber)
        {
            if (!arena.BeginBattle(request.HeaderUserNumber, opponentNumber))
            {
                return ProtocolResult.Refused("cannot start this arena battle");
            }

            quests.Add(request.HeaderUserNumber, clock.GetUtcNow().ToUnixTimeSeconds() / 86400, 5300006, 1);
        }

        UserInfo info = profiles.BuildUserInfo(opponent);
        IReadOnlyList<OwnedHeroRecord> owned = inventory.GetHeroes(opponentNumber);
        OwnedHeroRecord? hero = owned.FirstOrDefault(h => h.HeroPcid == info.ActiveHeroPcid) ?? (owned.Count > 0 ? owned[0] : null);
        // The comparison popup uses these same three slots. The 402 parser writes rows into a fixed native array.
        IReadOnlyList<FellowInfo> party = fellows.GetProfileParty(opponentNumber);
        HashSet<long> partyNumbers = party.Where(fellow => fellow.FellowNumber > 0).Select(fellow => fellow.FellowNumber).ToHashSet();
        logger.LogInformation("Arena battle start vs {Opponent}", opponentNumber);
        return ProtocolResult.Success(new ArenaBattleStartResponse
        {
            DailyQuest = Quests.DailyQuestInfo.Current(quests, clock, request.HeaderUserNumber, 5300006),
            Snapshot = new PvpSnapshot
            {
                User = friends.ToInfo(opponent, request.HeaderUserNumber),
                Hero = hero is null ? null : inventory.BuildHeroInfo(opponentNumber, hero.HeroNumber, hero.HeroPcid),
                Rank = arena.GetInfo(opponentNumber).Rank,
                Allies = party,
                AllyItems = fellowItems.GetOwned(opponentNumber).Where(item => partyNumbers.Contains(item.FellowNumber)).ToList(),
            },
            Master = new PvpMasterInfo(),
            Goods = profiles.GetGoods(request.HeaderUserNumber),
        });
    }
}

public sealed record PvpSnapshot
{
    [JsonPropertyName("user_info")] public FriendInfo User { get; init; } = new();
    [JsonPropertyName("hero_info")] public HeroInfo? Hero { get; init; }
    [JsonPropertyName("pvp_rank")] public long Rank { get; init; }
    [JsonPropertyName("fellow_info")] public IReadOnlyList<FellowInfo> Allies { get; init; } = [];
    [JsonPropertyName("fellow_item_infos")] public IReadOnlyList<FellowItemInfo> AllyItems { get; init; } = [];
}

/// <summary>master_pvp_info: the battle's tuning, dt_etc_setting's values.</summary>
public sealed record PvpMasterInfo
{
    [JsonPropertyName("Pvp_PcHpBuff")] public long PcHpBuff { get; init; } = 40;
    [JsonPropertyName("Pvp_FellowHealBuff")] public long FellowHealBuff { get; init; } = 5;
    [JsonPropertyName("Pvp_FellowGuardBuff")] public long FellowGuardBuff { get; init; } = 5;
    [JsonPropertyName("ResistancePvpDeleteTime")] public long ResistanceDeleteTime { get; init; } = 60000;
    [JsonPropertyName("ResistancePvpResetTime")] public long ResistanceResetTime { get; init; } = 10000;
    [JsonPropertyName("ResistancePvpMaxCount")] public long ResistanceMaxCount { get; init; } = 3;
}

public sealed record ArenaBattleStartResponse
{
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("daily_quest_info")] public required Quests.DailyQuestInfo DailyQuest { get; init; }
    [JsonPropertyName("pvp_user_info")] public PvpSnapshot Snapshot { get; init; } = new();
    [JsonPropertyName("master_pvp_info")] public PvpMasterInfo Master { get; init; } = new();
}

/// <summary>Protocol 403 (parser case @0x15a442): the battle result. Many combat stats come along; only the result code is used.</summary>
public sealed class ArenaResultHandler(PlayerDirectory directory, ArenaService arena, PlayerProfileService profiles, ILogger<ArenaResultHandler> logger) : IProtocolHandler
{
    private static double Share(GatewayRequest request, string hp, string max) =>
        request.Parameters.GetInt64(max) is > 0 and var total ? (double)request.Parameters.GetInt64(hp) / total : 0;

    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long opponentNumber = request.Parameters.GetInt64("pvp_user_no");
        // The client sends pvp_result 0 whatever happened, so the winner is read from the hit points left.
        // A knock-out is the best win; at time up the side with the larger share of its hit points left wins (a 1% margin goes to the player,
        // as the client's own banner did for a 7866/8000 against 7871/8000 finish).
        double mine = Share(request, "pvp_player_hp", "pvp_player_max_hp");
        double theirs = Share(request, "pvp_enemy_hp", "pvp_enemy_max_hp");
        long result = theirs <= 0 && mine > 0 ? ArenaRules.KnockOutResult : mine + 0.01 >= theirs ? ArenaRules.WinResult : ArenaRules.LossResult;
        logger.LogInformation("Arena result: {Params}", request.Parameters.ToJsonString());
        long? accepted = arena.CompleteBattle(userNumber, opponentNumber, result, request.Parameters.GetInt64("give_up") != 0);
        if (accepted is null)
        {
            return ProtocolResult.Refused("no matching arena battle or no Battle Spirits");
        }

        IReadOnlyList<long> grades = arena.SettleMissions(userNumber);
        return ProtocolResult.Success(new ArenaResultResponse
        {
            IsReward = grades.Count > 0 ? 1 : 0,
            Grades = grades,
            Info = arena.GetInfo(userNumber),
            OpponentNumber = opponentNumber,
            Result = accepted.Value,
            Goods = profiles.GetGoods(userNumber),
        });
    }
}

public sealed record ArenaResultResponse
{
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("is_reward")] public long IsReward { get; init; }
    /// <summary>Paid mission grades (parser @0x161b70 keeps up to five for the Mission Complete boxes).</summary>
    [JsonPropertyName("grade")] public IReadOnlyList<long> Grades { get; init; } = [];
    [JsonPropertyName("user_pvp_ranking_info")] public UserPvpInfo Info { get; init; } = new();
    [JsonPropertyName("pvp_user_no")] public long OpponentNumber { get; init; }
    [JsonPropertyName("pvp_result")] public long Result { get; init; }
}

/// <summary>
/// Protocol 404 (parser case @0x15ce72 reads <c>reward_pvp_info</c> with ParsingDataToRewardTable, one ranking-reward row). Season
/// rewards are mailed when the season rolls over, so this only reports the row last mailed (empty object when none); it pays nothing.
/// </summary>
public sealed class ArenaRewardHandler(PlayerDirectory directory, ArenaService arena) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        arena.CurrentSeason();
        return ProtocolResult.Success(new ArenaRewardResponse
        {
            Reward = arena.LastRankingReward(userNumber) is { } row ? row : EmptyObject.Instance,
        });
    }
}

public sealed record ArenaRewardResponse
{
    [JsonPropertyName("reward_pvp_info")] public object Reward { get; init; } = EmptyObject.Instance;
}

/// <summary>Protocol 407 (parser case @0x15eb86): refill the Battle Spirits for gems.</summary>
public sealed class ArenaRecoverHandler(PlayerDirectory directory, PlayerProfileService profiles, ArenaService arena) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        if (!arena.NeedsSpirit(userNumber))
        {
            return ProtocolResult.Refused("Battle Spirits are already full");
        }
        long price = arena.NextRecoveryGems(userNumber);
        if (!profiles.TrySpend(userNumber, Rules.CurrencyItems.Gems, price))
        {
            return ProtocolResult.Refused("not enough gems");
        }

        arena.RecordRecovery(userNumber);
        arena.FillSpirit(userNumber);
        return ProtocolResult.Success(new ArenaRecoverResponse(profiles.GetGoods(userNumber), new PvpRecovery(arena.NextRecoveryGems(userNumber))));
    }
}

public sealed record ArenaRecoverResponse(
    [property: JsonPropertyName("update_item_etc_info")] IReadOnlyList<GoodsEntry> Goods,
    [property: JsonPropertyName("master_pvp_recovery_info")] PvpRecovery Recovery);
