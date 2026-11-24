namespace KnS.Data.Repositories;

/// <summary>An ally mission on a player's board (fellow_mission). Number lists are comma separated, rewards are "item:value;...".</summary>
public sealed record FellowMissionRecord
{
    public long Id { get; init; }
    public long MissionId { get; init; }
    public long Status { get; init; }
    public string Fellows { get; init; } = "";
    public string Soldiers { get; init; } = "";
    public long EndTime { get; init; }
    public long SuccessRate { get; init; }
    public string Rewards { get; init; } = "";
}

/// <summary>A row of master_fellow_mission (dt_fellow_pkg_140 table 3, see tools/dump_fellow_missions.py).</summary>
public sealed record FellowMissionRule
{
    public long MissionId { get; init; }
    public long Grade { get; init; }

    /// <summary>Int 3 (+0xc): exported as level; GetFellowScore compares it with the ally's country (ally row int 114).</summary>
    public long Level { get; init; }

    /// <summary>Int 5 (+0x14), exported as c5; GetFellowScore compares it with the ally's type (ally row int 115).</summary>
    public long AllyType { get; init; }

    /// <summary>Int 41 (+0xa4, text_index in CSV): the mission EXP shown by DrawMissionTable, DrawMissionDetail, and DrawFellowMissionResult.</summary>
    public long Experience { get; init; }

    /// <summary>Int 4 (+0x10, duration in CSV): progression/tier score (0..17528) grouping missions into reward tiers.</summary>
    public long TierScore { get; init; }

    /// <summary>Int 2 (+8): maximum selected allies, as read by the browser mission picker.</summary>
    public long AllyCount { get; init; }

    /// <summary>Int 6: how long the mission runs, in hours.</summary>
    public long Hours { get; init; }

    public IReadOnlyList<long> Conditions { get; init; } = [];

    /// <summary>(item id, min, max) for up to six rewards.</summary>
    public IReadOnlyList<(long Item, long Min, long Max)> Rewards { get; init; } = [];

    /// <summary>Four country/value pairs at native master offsets 0x84 through 0xa0.</summary>
    public IReadOnlyList<(long Country, long Value)> Reputation { get; init; } = [];
}

public sealed class FellowMissionRepository(DataSession session)
{
    private const string Columns =
        "rowid_alias AS Id, mission_id AS MissionId, status AS Status, fellows AS Fellows, soldiers AS Soldiers,"
        + " end_time AS EndTime, success_rate AS SuccessRate, rewards AS Rewards";

    public IReadOnlyList<FellowMissionRecord> GetAll(long userNumber) =>
        session.Query<FellowMissionRecord>($"SELECT {Columns} FROM fellow_mission WHERE user_no=@userNumber ORDER BY rowid_alias", new { userNumber });

    public FellowMissionRecord? Find(long userNumber, long missionId) =>
        session.QueryFirstOrDefault<FellowMissionRecord>(
            $"SELECT {Columns} FROM fellow_mission WHERE user_no=@userNumber AND mission_id=@missionId", new { userNumber, missionId });

    public void Add(long userNumber, long missionId, string rewards) =>
        session.Execute(
            "INSERT INTO fellow_mission(user_no, mission_id, status, fellows, soldiers, rewards) VALUES(@userNumber, @missionId, 0, '', '', @rewards)",
            new { userNumber, missionId, rewards });

    public void Start(long id, string fellows, string soldiers, long endTime, long successRate) =>
        session.Execute(
            "UPDATE fellow_mission SET status=1, fellows=@fellows, soldiers=@soldiers, end_time=@endTime, success_rate=@successRate WHERE rowid_alias=@id",
            new { id, fellows, soldiers, endTime, successRate });

    public void Delete(long id) => session.Execute("DELETE FROM fellow_mission WHERE rowid_alias=@id", new { id });

    public void DeleteOpen(long userNumber) =>
        session.Execute("DELETE FROM fellow_mission WHERE user_no=@userNumber AND status=0", new { userNumber });

    public FellowMissionRecord? GetTutorial(long userNumber) =>
        session.QueryFirstOrDefault<FellowMissionRecord>(
            "SELECT 6220000 AS MissionId,status AS Status,fellows AS Fellows,soldiers AS Soldiers,end_time AS EndTime,100 AS SuccessRate,'' AS Rewards FROM fellow_mission_tutorial WHERE user_no=@userNumber",
            new { userNumber });

    public void StartTutorial(long userNumber, string fellows, string soldiers, long endTime) =>
        session.Execute("INSERT INTO fellow_mission_tutorial(user_no,status,fellows,soldiers,end_time) VALUES(@userNumber,1,@fellows,@soldiers,@endTime)",
            new { userNumber, fellows, soldiers, endTime });

    public void ClaimTutorial(long userNumber) =>
        session.Execute("UPDATE fellow_mission_tutorial SET status=3 WHERE user_no=@userNumber AND status=1", new { userNumber });

    /// <summary>FreeSoldiersDay and RefreshDay are UTC days (unix seconds / 86400).</summary>
    public (long NextRefresh, long FreeSoldiersUsed, long FreeSoldiersDay, long RefreshCount, long RefreshDay) GetState(long userNumber)
    {
        var state = session.QueryRows(
            "SELECT next_refresh, free_soldiers_used, free_soldiers_day, refresh_count, refresh_day FROM fellow_mission_state WHERE user_no=@userNumber", new { userNumber });
        return state.Count == 0 ? (0, 0, 0, 0, 0)
            : (Convert.ToInt64(state[0]["next_refresh"]), Convert.ToInt64(state[0]["free_soldiers_used"]), Convert.ToInt64(state[0]["free_soldiers_day"]),
               Convert.ToInt64(state[0]["refresh_count"]), Convert.ToInt64(state[0]["refresh_day"]));
    }

    public void SetState(long userNumber, long nextRefresh, long freeSoldiersUsed, long freeSoldiersDay, long refreshCount, long refreshDay) =>
        session.Execute(
            $"{session.Dialect.InsertOrReplace} INTO fellow_mission_state(user_no, next_refresh, free_soldiers_used, free_soldiers_day, refresh_count, refresh_day) VALUES(@userNumber, @nextRefresh, @freeSoldiersUsed, @freeSoldiersDay, @refreshCount, @refreshDay)",
            new { userNumber, nextRefresh, freeSoldiersUsed, freeSoldiersDay, refreshCount, refreshDay });

    // ------------------------------------------------------------------ master data

    public IReadOnlyDictionary<long, long> GetReputation(long userNumber) =>
        session.QueryRows("SELECT country, amount FROM fellow_mission_reputation WHERE user_no=@userNumber", new { userNumber })
            .ToDictionary(row => Convert.ToInt64(row["country"]), row => Convert.ToInt64(row["amount"]));

    public void AddReputation(long userNumber, long country, long value)
    {
        if (country is < 1 or > 11 || value <= 0)
        {
            return;
        }

        session.Execute($"{session.Dialect.InsertOrIgnore} INTO fellow_mission_reputation(user_no,country,amount) VALUES(@userNumber,@country,0)",
            new { userNumber, country });
        session.Execute("UPDATE fellow_mission_reputation SET amount=amount+@value WHERE user_no=@userNumber AND country=@country",
            new { userNumber, country, value });
    }

    public FellowMissionRule? FindRule(long missionId) =>
        session.QueryRows("SELECT * FROM master_fellow_mission WHERE mission_id=@missionId", new { missionId }).Select(ToRule).FirstOrDefault();

    /// <summary>Missions within a progression tier band (int 4 / duration column, 0..17528). Level in the CSV is Country ID, not player level.</summary>
    public IReadOnlyList<(long MissionId, long Grade)> GetMissionPool(long minTierScore, long maxTierScore) =>
        session.QueryRows(
                "SELECT mission_id, grade FROM master_fellow_mission WHERE mission_id<6220000 AND duration>=@minTierScore AND duration<=@maxTierScore ORDER BY mission_id",
                new { minTierScore, maxTierScore })
            .Select(row => (Convert.ToInt64(row["mission_id"]), Convert.ToInt64(row["grade"])))
            .ToList();

    /// <summary>
    /// What GetFellowScore@0x56284 reads from an ally's master row (dt_fellow_pkg_140 table 0):
    /// int 1 grade, int 114 country, int 115 type. Null for an unknown ally.
    /// </summary>
    public (long Grade, long Country, long Type)? GetMissionProfile(long fellowId)
    {
        var rows = session.QueryRows("SELECT grade, raw_hex FROM master_fellow WHERE fellow_id=@fellowId", new { fellowId });
        if (rows.Count == 0)
        {
            return null;
        }

        string hex = Convert.ToString(rows[0]["raw_hex"], System.Globalization.CultureInfo.InvariantCulture) ?? "";
        long Int(int index) => hex.Length >= (index + 1) * 8
            ? System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(hex.AsSpan(index * 8, 8)))
            : 0;
        return (Convert.ToInt64(rows[0]["grade"], System.Globalization.CultureInfo.InvariantCulture), Int(114), Int(115));
    }

    /// <summary>All five mission skill families an ally can have (master_fellow_skill), without the empty ones.</summary>
    public IReadOnlyList<long> GetAllSkills(long fellowId)
    {
        var rows = session.QueryRows("SELECT skill1, skill2, skill3, skill4, skill5 FROM master_fellow_skill WHERE fellow_id=@fellowId", new { fellowId });
        return rows.Count == 0 ? [] : rows[0].Values.Select(value => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)).Where(skill => skill > 0).ToList();
    }

    /// <summary>The two mission skills an ally shows: the first two of its five (master_fellow_skill).</summary>
    public IReadOnlyList<long> GetSkills(long fellowId)
    {
        var rows = session.QueryRows("SELECT skill1, skill2 FROM master_fellow_skill WHERE fellow_id=@fellowId", new { fellowId });
        return rows.Count == 0 ? [] : [Convert.ToInt64(rows[0]["skill1"]), Convert.ToInt64(rows[0]["skill2"])];
    }

    private static FellowMissionRule ToRule(IReadOnlyDictionary<string, object?> row)
    {
        long Get(string name) => Convert.ToInt64(row[name]);
        return new FellowMissionRule
        {
            MissionId = Get("mission_id"),
            Grade = Get("grade"),
            Level = Get("level"),
            AllyType = Get("c5"),
            Experience = Get("text_index"),
            TierScore = Get("duration"),
            AllyCount = Get("mission_type"),
            Hours = Get("c6"),
            Conditions = Enumerable.Range(1, 8).Select(i => Get($"cond{i}")).Where(c => c > 0).ToList(),
            Rewards = Enumerable.Range(1, 6).Select(i => (Get($"reward{i}_item"), Get($"reward{i}_min"), Get($"reward{i}_max"))).Where(r => r.Item1 > 0).ToList(),
            Reputation = Enumerable.Range(1, 4).Select(i => (Get($"reputation{i}_country"), Get($"reputation{i}_value")))
                .Where(pair => pair.Item1 is >= 1 and <= 11 && pair.Item2 > 0).ToList(),
        };
    }
}
