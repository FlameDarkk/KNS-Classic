using KnS.Game.Rewards;
using System.Text.Json.Nodes;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Players;
using KnS.Game.Randomness;
using KnS.Game.Rules;

namespace KnS.Game.Missions;

/// <summary>
/// Ally missions: each player has a board of <see cref="BoardSize"/> missions drawn from master_fellow_mission. Allies sent on one are busy
/// until it ends; the success chance is the client's own formula (<see cref="SuccessRate"/>), mercenaries included.
/// The board size and the refresh price here are guesses.
/// </summary>
public sealed class FellowMissionService(
    FellowMissionRepository missions,
    FellowRepository fellows,
    FellowService fellowService,
    PlayerRepository players,
    BuffRepository buffs,
    RewardGrantService rewardGrants,
    KnS.Game.Stages.StoryService stories,
    IRandomSource random,
    TimeProvider clock)
{
    public const int BoardSize = 8;
    /// <summary>Type-5 mercenaries the client lets a player send for free (3 - free_soldier_use_count).</summary>
    public const long FreeSoldiers = 3;
    /// <summary>
    /// Native dt_etc_setting.json FellowMission refresh prices: 1st refresh = 5 Gems, 2nd = 10, ..., 19th = 95, 20th+ = 100 Gems.
    /// </summary>
    public static long RefreshPriceForCount(long refreshesUsedToday) => Math.Min(100, 5 * (Math.Max(0, refreshesUsedToday) + 1));

    public long NextRefreshPrice(long userNumber)
    {
        (_, _, _, long count, long day) = missions.GetState(userNumber);
        return RefreshPriceForCount(day == Today ? count : 0);
    }

    public void RecordRefresh(long userNumber)
    {
        (long nextRefresh, long used, long usedDay, long count, long day) = missions.GetState(userNumber);
        long nextCount = (day == Today ? count : 0) + 1;
        missions.SetState(userNumber, nextRefresh, used, usedDay, nextCount, Today);
    }
    public const long StatusOpen = 0;
    public const long StatusRunning = 1;
    public const long StatusDone = 2;
    public const long TutorialMissionId = 6220000;

    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    public IReadOnlyList<FellowMissionRecord> GetListedMissions(long userNumber)
    {
        IReadOnlyList<FellowMissionRecord> board = GetBoard(userNumber);
        return missions.GetTutorial(userNumber) is { Status: StatusRunning } tutorial ? [.. board, tutorial] : board;
    }

    /// <summary>
    /// Restoration choice: grade weights (Common > Magic > Rare > Heroic > Legend) for board slots.
    /// dt_fellow_pkg_140 table 5 indices 35-39 are 50, 35, 10, 5, 0 (which would omit Legend completely).
    /// </summary>
    private static readonly (long Grade, long Weight)[] GradeWeights = [(1, 45), (2, 30), (3, 15), (4, 7), (5, 3)];

    /// <summary>Tops the board up to <see cref="BoardSize"/> missions using progression tier bands and rarity weights.</summary>
    public IReadOnlyList<FellowMissionRecord> GetBoard(long userNumber)
    {
        IReadOnlyList<FellowMissionRecord> board = missions.GetAll(userNumber);
        if (board.Count >= BoardSize)
        {
            return board;
        }

        long level = players.FindAccount(userNumber)?.Level ?? 1;
        (long minTier, long maxTier) = TierRangeForLevel(level);
        HashSet<long> present = board.Select(m => m.MissionId).ToHashSet();
        var byGrade = new Dictionary<long, List<long>>();
        foreach ((long missionId, long grade) in missions.GetMissionPool(minTier, maxTier))
        {
            if (present.Contains(missionId))
            {
                continue;
            }

            if (!byGrade.TryGetValue(grade, out List<long>? list))
            {
                list = [];
                byGrade[grade] = list;
            }

            list.Add(missionId);
        }

        while (present.Count < BoardSize && PickWeightedMissionId(byGrade) is { } id)
        {
            if (present.Add(id) && missions.FindRule(id) is { } rule)
            {
                missions.Add(userNumber, id, RollRewards(rule));
            }
        }

        return missions.GetAll(userNumber);
    }

    /// <summary>Maps player level to the mission TierScore (int 4) band so low-tier Rare missions do not mix with high-tier Magic/Common ones.</summary>
    public static (long MinTier, long MaxTier) TierRangeForLevel(long level) => level switch
    {
        < 10 => (0, 1500),
        < 20 => (500, 4500),
        < 30 => (2500, 9500),
        < 40 => (5500, 14500),
        _ => (9000, 20000),
    };

    private long? PickWeightedMissionId(Dictionary<long, List<long>> byGrade)
    {
        long totalWeight = GradeWeights.Where(entry => byGrade.TryGetValue(entry.Grade, out List<long>? list) && list.Count > 0)
            .Sum(entry => entry.Weight);
        if (totalWeight <= 0)
        {
            return null;
        }

        long roll = random.NextBelow(totalWeight);
        long cumulative = 0;
        foreach ((long grade, long weight) in GradeWeights)
        {
            if (!byGrade.TryGetValue(grade, out List<long>? list) || list.Count == 0)
            {
                continue;
            }

            cumulative += weight;
            if (roll < cumulative)
            {
                int index = (int)random.NextBelow(list.Count);
                long id = list[index];
                list.RemoveAt(index);
                return id;
            }
        }

        return null;
    }

    /// <summary>Replaces the missions that are not running.</summary>
    public IReadOnlyList<FellowMissionRecord> Refresh(long userNumber)
    {
        missions.DeleteOpen(userNumber);
        return GetBoard(userNumber);
    }

    /// <summary>The allies on running missions.</summary>
    public HashSet<long> GetBusyFellows(long userNumber)
    {
        HashSet<long> busy = missions.GetAll(userNumber).Where(m => m.Status == StatusRunning).SelectMany(m => Numbers(m.Fellows)).ToHashSet();
        if (missions.GetTutorial(userNumber) is { Status: StatusRunning } tutorial)
        {
            busy.UnionWith(Numbers(tutorial.Fellows));
        }

        return busy;
    }

    /// <summary>326 starts the isolated tutorial mission. Its row defines no rewards or reputation.</summary>
    public FellowMissionRecord? StartTutorial(long userNumber, long missionId, IReadOnlyList<long> selected, IReadOnlyList<long> soldiers)
    {
        if (missionId != TutorialMissionId || missions.GetTutorial(userNumber) is not null
            || missions.FindRule(missionId) is not { } rule || selected.Count == 0 || selected.Count > rule.AllyCount
            || selected.Distinct().Count() != selected.Count || soldiers.Count > 0)
        {
            return null;
        }

        HashSet<long> busy = GetBusyFellows(userNumber);
        IReadOnlyList<FellowRecord> owned = fellows.GetOwned(userNumber);
        if (selected.Any(number => busy.Contains(number) || !owned.Any(fellow => fellow.FellowNumber == number && !FellowRules.IsAngel(fellow.FellowId))))
        {
            return null;
        }

        missions.StartTutorial(userNumber, string.Join(',', selected), "", Now + rule.Hours * 3600);
        return missions.GetTutorial(userNumber);
    }

    /// <summary>327 is the free tutorial result check: native code bypasses the ordinary early-check price.</summary>
    public FellowMissionRecord? ClaimTutorial(long userNumber, long missionId)
    {
        if (missionId != TutorialMissionId || missions.GetTutorial(userNumber) is not { Status: StatusRunning } tutorial)
        {
            return null;
        }

        missions.ClaimTutorial(userNumber);
        return tutorial with { EndTime = Now };
    }

    /// <summary>
    /// Starts a mission with exactly the allies the player selected; a start may also send only
    /// mercenaries. Null when it cannot start (not open, an ally missing or busy, nobody sent).
    /// </summary>
    public FellowMissionRecord? Start(long userNumber, long missionId, IReadOnlyList<long> fellowNumbers, IReadOnlyList<long> soldierIds)
    {
        FellowMissionRecord? mission = missions.Find(userNumber, missionId);
        FellowMissionRule? rule = missions.FindRule(missionId);
        if (mission is not { Status: StatusOpen } || rule is null || (fellowNumbers.Count == 0 && soldierIds.Count == 0))
        {
            return null;
        }

        HashSet<long> owned = fellows.GetOwnedFellowNumbers(userNumber);
        HashSet<long> busy = GetBusyFellows(userNumber);
        if (fellowNumbers.Count > 10 || fellowNumbers.Count > rule.AllyCount || fellowNumbers.Any(number => !owned.Contains(number) || busy.Contains(number))
            || fellowNumbers.Distinct().Count() != fellowNumbers.Count
            || fellows.GetOwned(userNumber).Any(fellow => fellowNumbers.Contains(fellow.FellowNumber) && FellowRules.IsAngel(fellow.FellowId)))
        {
            return null;
        }

        // Keep the selection order: the client's slots, and which ally counts first for a condition.
        Dictionary<long, FellowRecord> byNumber = fellows.GetOwned(userNumber).ToDictionary(f => f.FellowNumber);
        var allies = new List<MissionAlly>();
        foreach (long number in fellowNumbers)
        {
            FellowRecord fellow = byNumber[number];
            (long grade, long country, long type) = missions.GetMissionProfile(fellow.FellowId) ?? (0, 0, 0);
            allies.Add(new MissionAlly(grade, fellow.Level, fellow.Upgrade, country, type, fellowService.GetTalents(fellow)));
        }

        long rate = SuccessRate(rule, allies, soldierIds);
        missions.Start(mission.Id, string.Join(',', fellowNumbers), string.Join(',', soldierIds), Now + rule.Hours * 3600, rate);
        return missions.Find(userNumber, missionId);
    }

    /// <summary>
    /// dt_fellow_pkg_140 table 5, the single row GetFellowMissionValue@0xf5de4 returns: ints 0-4 the
    /// per-ally target by mission grade, 5-10 the ally grade score, 11-14 the level score
    /// (Lv. 1-19 / 20-24 / 25-29 / 30+), 15-19 the +1..+5 power-up score, 20 / 21 the country /
    /// type match, 21 + skill level the condition score (levels 1-8 read 22-29), 30-34 the mercenary of type 1-5.
    /// </summary>
    public static readonly IReadOnlyList<long> MissionValues =
    [
        20, 40, 60, 80, 100, 1, 2, 4, 8, 16, 25, 4, 8, 16, 25, 4, 8, 12, 16, 20, 5, 5, 2, 5, 10, 10, 20, 30, 40, 50,
        5, 10, 15, 20, 25, 50, 35, 10, 5, 0, 10, 180, 1620, 6912, 14580, 27090,
    ];

    /// <summary>An ally as GetFellowScore reads it: master grade / country / type, its level, power-up and two mission skills.</summary>
    public sealed record MissionAlly(long Grade, long Level, long Upgrade, long Country, long Type, IReadOnlyList<long> Skills);

    /// <summary>
    /// The chance in percent, as the client computes and shows it (GetSuccessRate@0x563d8):
    /// 100 * the allies' scores / (allowed allies * the grade's per-ally target), plus each
    /// mercenary's bonus, at most 100.
    /// </summary>
    public static long SuccessRate(FellowMissionRule rule, IReadOnlyList<MissionAlly> allies, IReadOnlyList<long> soldierTypes)
    {
        // SetAttribute@0x561dc: each condition counts for the first ally with its highest skill level.
        var best = new Dictionary<long, MissionAlly>();
        foreach (long condition in rule.Conditions)
        {
            long top = 0;
            foreach (MissionAlly ally in allies)
            {
                long level = AttributeLevel(ally, condition);
                if (level > top)
                {
                    top = level;
                    best[condition] = ally;
                }
            }
        }

        long score = 0;
        foreach (MissionAlly ally in allies)
        {
            long value = Value(ally.Grade + 4) + Value(ally.Level >= 30 ? 14 : ally.Level >= 25 ? 13 : ally.Level >= 20 ? 12 : 11);
            if (ally.Upgrade > 0) value += Value(ally.Upgrade + 14);
            if (ally.Country == rule.Level) value += Value(20);
            if (ally.Type == rule.AllyType) value += Value(21);
            foreach (long condition in rule.Conditions)
            {
                long level = AttributeLevel(ally, condition);
                if (level > 0 && best.TryGetValue(condition, out MissionAlly? owner) && ReferenceEquals(owner, ally))
                {
                    value += Value(level + 21);
                }
            }

            score += value;
        }

        long target = Math.Max(1, rule.AllyCount) * Value(rule.Grade - 1);
        long rate = target > 0 ? 100 * score / target : 0;
        rate += soldierTypes.Where(type => type is >= 1 and <= 5).Sum(type => Value(type + 29));
        return Math.Clamp(rate, 0, 100);
    }

    private static long Value(long index) => index >= 0 && index < MissionValues.Count ? MissionValues[(int)index] : 0;

    /// <summary>GetFellowAttributeValue@0x56108: a skill of the condition's family (id / 10) gives its level (id % 10).</summary>
    private static long AttributeLevel(MissionAlly ally, long condition) =>
        ally.Skills.Where(skill => skill / 10 == condition / 10).Select(skill => Math.Max(0, skill % 10)).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Settles the missions that have ended: pays the rewards of the ones that succeed (rolled against the success rate), removes all of
    /// them from the board. Returns (succeeded, failed).
    /// </summary>
    public (IReadOnlyList<FellowMissionRecord> Succeeded, IReadOnlyList<FellowMissionRecord> Failed, IReadOnlyList<long> AwardedGear) Claim(
        long userNumber, IEnumerable<long> missionIds, bool payToFinish = false)
    {
        var succeeded = new List<FellowMissionRecord>();
        var failed = new List<FellowMissionRecord>();
        var awardedGear = new List<long>();
        foreach (long id in missionIds.Distinct())
        {
            if (missions.Find(userNumber, id) is not { Status: StatusRunning } mission)
            {
                continue;
            }

            if (mission.EndTime > Now)
            {
                // Check the result early for gems: one per hour still to wait.
                long price = (mission.EndTime - Now + 3599) / 3600;
                if (!payToFinish || !players.TrySpendGems(userNumber, price))
                {
                    continue;
                }
            }

            if (random.NextBelow(100) < mission.SuccessRate)
            {
                Pay(userNumber, mission, awardedGear);
                succeeded.Add(mission);
            }
            else
            {
                failed.Add(mission);
            }

            missions.Delete(mission.Id);
        }

        return (succeeded, failed, awardedGear);
    }

    /// <summary>Gives a running mission up: the allies come home, the mission leaves the board.</summary>
    public bool Cancel(long userNumber, long missionId)
    {
        if (missions.Find(userNumber, missionId) is not { Status: StatusRunning } mission)
        {
            return false;
        }

        missions.Delete(mission.Id);
        return true;
    }

    /// <summary>Ends a running mission now, so that it can be claimed.</summary>
    public FellowMissionRecord? Finish(long userNumber, long missionId)
    {
        if (missions.Find(userNumber, missionId) is not { Status: StatusRunning } mission)
        {
            return null;
        }

        missions.Start(mission.Id, mission.Fellows, mission.Soldiers, Now, mission.SuccessRate);
        return missions.Find(userNumber, missionId);
    }

    private long Today => Now / 86400;

    /// <summary>
    /// The free mercenary count resets each UTC day, like the daily quests (restoration choice: the client only shows
    /// 3 - free_soldier_use_count and carries no reset time). A count stored on an earlier day reads as 0.
    /// </summary>
    public (long NextRefresh, long FreeSoldiersUsed) GetState(long userNumber)
    {
        (long nextRefresh, long used, long day, _, _) = missions.GetState(userNumber);
        return (nextRefresh, day == Today ? used : 0);
    }

    /// <summary>
    /// The Gems a start costs, as the client's "use %d Gems to start the mission?" check computes it
    /// (Scene_PointerReleaseEvent_SC_POPUP_FELLOW_MISSION @0x5e65c): each mercenary of type t costs mission value 24 + t
    /// (10/20/30/40/50 Gems), minus up to <see cref="FreeSoldiers"/> - used type-5 mercenaries at their price.
    /// With a zero price the client starts without asking. Returns the price and the free mercenaries it uses.
    /// </summary>
    public static (long Price, long FreeUsed) SoldierPrice(IReadOnlyList<long> soldierTypes, long freeSoldiersUsed)
    {
        long price = soldierTypes.Where(type => type is >= 1 and <= 5).Sum(type => Value(type + 24));
        long free = Math.Clamp(Math.Min(FreeSoldiers - freeSoldiersUsed, soldierTypes.Count(type => type == 5)), 0, FreeSoldiers);
        return (price - free * Value(29), free);
    }

    public void UseFreeSoldiers(long userNumber, long count)
    {
        (long nextRefresh, long used, long usedDay, long refreshCount, long refreshDay) = missions.GetState(userNumber);
        long nextUsed = (usedDay == Today ? used : 0) + count;
        missions.SetState(userNumber, nextRefresh, nextUsed, Today, refreshCount, refreshDay);
    }

    public IReadOnlyDictionary<long, long> GetReputation(long userNumber) => missions.GetReputation(userNumber);

    public void SetNextRefresh(long userNumber, long nextRefresh)
    {
        (_, long used, long day, long refreshCount, long refreshDay) = missions.GetState(userNumber);
        missions.SetState(userNumber, nextRefresh, used, day, refreshCount, refreshDay);
    }

    public long RemainingSeconds(FellowMissionRecord mission) =>
        mission.Status == StatusRunning ? Math.Max(0, mission.EndTime - Now) : 0;

    /// <summary>
    /// A failed mission for the claim reply (325): the Mission Report sums reward_idN/reward_valueN over every listed mission, success
    /// and fail alike, but a failed mission pays nothing, so its rewards are sent as 0.
    /// </summary>
    public JsonObject ToFailedInfo(FellowMissionRecord mission)
    {
        JsonObject info = ToInfo(mission);
        for (int i = 1; i <= 6; i++)
        {
            info[$"reward_id{i}"] = 0;
            info[$"reward_value{i}"] = 0;
        }

        return info;
    }

    /// <summary>The mission as the client reads it (ParsingDataToFellowMissionInfo@0x14b6d9).</summary>
    public JsonObject ToInfo(FellowMissionRecord mission)
    {
        var info = new JsonObject { ["mission_id"] = mission.MissionId };
        long[] sent = Numbers(mission.Fellows).ToArray();
        for (int i = 1; i <= 10; i++)
        {
            info[$"fellow_no{i}"] = i <= sent.Length ? sent[i - 1] : 0;
        }

        (long Item, long Value)[] rewards = ParseRewards(mission.Rewards).ToArray();
        for (int i = 1; i <= 6; i++)
        {
            info[$"reward_id{i}"] = i <= rewards.Length ? rewards[i - 1].Item : 0;
            info[$"reward_value{i}"] = i <= rewards.Length ? rewards[i - 1].Value : 0;
        }

        long[] soldiers = Numbers(mission.Soldiers).ToArray();
        for (int i = 1; i <= 5; i++)
        {
            info[$"soldier_count{i}"] = soldiers.Count(s => s == i);
        }

        // Native Start checks status == 1. Wire states are open=1, running=2, ready=3;
        // retain persisted open=0/running=1 so existing attempts and busy allies stay intact.
        info["status"] = mission.Status == StatusOpen ? 1
            : mission.Status == StatusRunning && mission.EndTime > Now ? 2 : 3;
        info["mission_end_remain_time"] = RemainingSeconds(mission);
        info["success_rate"] = mission.SuccessRate;
        return info;
    }

    private string RollRewards(FellowMissionRule rule) =>
        string.Join(';', rule.Rewards.Select(r => $"{r.Item}:{r.Min + random.NextBelow(r.Max - r.Min + 1)}"));

    private void Pay(long userNumber, FellowMissionRecord mission, List<long> awardedGear)
    {
        if (missions.FindRule(mission.MissionId) is { } reputationRule)
        {
            foreach ((long country, long value) in reputationRule.Reputation)
            {
                missions.AddReputation(userNumber, country, value);
            }
        }

        if (missions.FindRule(mission.MissionId) is { Experience: > 0 } rule)
        {
            long experience = Protocols.Items.ExpBoostRules.Apply(rule.Experience,
                buffs.IsActive(userNumber, Protocols.Items.ExpBoostRules.BuffType, Now));
            fellowService.AwardExperience(userNumber, Numbers(mission.Fellows), experience);
        }

        var granted = new GrantedRewards();
        List<(long Item, long Value)> rewards = ParseRewards(mission.Rewards).ToList();
        foreach ((long item, long value) in rewards)
        {
            rewardGrants.Grant(userNumber, item, value, granted);
        }

        stories.RecordGathered(userNumber, rewards);
        awardedGear.AddRange(granted.FellowItemNumbers);
    }

    private static IEnumerable<(long Item, long Value)> ParseRewards(string rewards) =>
        rewards.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split(':')).Where(p => p.Length == 2)
            .Select(p => (long.Parse(p[0]), long.Parse(p[1])));

    private static IEnumerable<long> Numbers(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse);
}
