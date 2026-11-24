namespace KnS.Data.Repositories;

public sealed record AchievementRule
{
    public long AchievementId { get; init; }
    public long Type { get; init; }
    public long Goal { get; init; }
    public long SubType { get; init; }
    public long PreviousId { get; init; }
    public long RewardItemId { get; init; }
    public long RewardCount { get; init; }
}

public sealed record AchievementClaimRow(long AchievementId);

public sealed record AchievementProgressRow
{
    public long Type { get; init; }
    public long SubType { get; init; }
    public long Count { get; init; }
}

/// <summary>Achievement rules (master_achievement, from dt_achieve_pkg) and the rewards each player has collected.</summary>
public sealed class AchievementRepository(DataSession session)
{
    public IReadOnlyList<AchievementRule> Rules() => session.Query<AchievementRule>(
        "SELECT achievement_id AS AchievementId, type AS Type, goal AS Goal, sub_type AS SubType, previous_id AS PreviousId,"
        + " reward_item_id AS RewardItemId, reward_count AS RewardCount FROM master_achievement ORDER BY achievement_id");

    public HashSet<long> Claimed(long userNumber) => session.Query<AchievementClaimRow>(
        "SELECT achievement_id AS AchievementId FROM achievement_claim WHERE user_no=@userNumber",
        new { userNumber }).Select(row => row.AchievementId).ToHashSet();

    /// <summary>Records the claim once; false when it was already collected.</summary>
    public bool TryClaim(long userNumber, long achievementId) => session.Execute(
        $"{session.Dialect.InsertOrIgnore} INTO achievement_claim(user_no, achievement_id) VALUES(@userNumber, @achievementId)",
        new { userNumber, achievementId }) == 1;

    /// <summary>The client-counted achievement totals (achievement_list), for the 121 list at login.</summary>
    public IReadOnlyList<AchievementProgressRow> Progress(long userNumber) => session.Query<AchievementProgressRow>(
        "SELECT type AS Type, sub_type AS SubType, count AS Count FROM achievement_progress WHERE user_no=@userNumber ORDER BY type, sub_type",
        new { userNumber }).ToList();

    /// <summary>Adds an accepted event, inside the same transaction as its owning operation.</summary>
    public void AddProgress(long userNumber, long type, long count)
    {
        if (count <= 0) return;
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO achievement_progress(user_no,type,sub_type,count) VALUES(@userNumber,@type,0,0)", new { userNumber, type });
        session.Execute("UPDATE achievement_progress SET count=CASE WHEN count>=9223372036854775807-@count THEN 9223372036854775807 ELSE count+@count END WHERE user_no=@userNumber AND type=@type AND sub_type=0", new { userNumber, type, count });
    }

    /// <summary>Count acquired equipment/material rows, including gifts and claimed dungeon offers.</summary>
    public void RecordItem(long userNumber, long itemId)
    {
        if (itemId is >= 200000 and < 1000000) AddProgress(userNumber, 40006, 1);
        else if (itemId is >= 1700000 and < 1800000) AddProgress(userNumber, 40007, 1);
    }

    /// <summary>Recover provable existing progress without inventing sold items, spent gold or past streaks.</summary>
    public void Reconcile(long userNumber)
    {
        long Scalar(string sql) => session.QueryFirstOrDefault<long>(sql, new { userNumber });
        RecordProgress(userNumber, 40000, 0, Scalar("SELECT level FROM gameuser WHERE user_no=@userNumber"));
        RecordProgress(userNumber, 40006, 0, Scalar("SELECT COUNT(*) FROM item WHERE user_no=@userNumber AND claimed=1 AND item_id>=200000 AND item_id<1000000"));
        RecordProgress(userNumber, 40007, 0, Scalar("SELECT COUNT(*) FROM item WHERE user_no=@userNumber AND claimed=1 AND item_id>=1700000 AND item_id<1800000"));
        RecordProgress(userNumber, 40009, 0, Scalar("SELECT gold FROM gameuser WHERE user_no=@userNumber"));
        RecordProgress(userNumber, 40057, 0, Scalar("SELECT COUNT(*) FROM user_hero WHERE user_no=@userNumber"));
        RecordProgress(userNumber, 40031, 0, Scalar("SELECT COALESCE(SUM(plays),0) FROM raid_score WHERE user_no=@userNumber"));
        RecordProgress(userNumber, 40082, 0, Scalar("SELECT COALESCE(MAX(streak),0) FROM (SELECT streak FROM pvp_stat WHERE user_no=@userNumber UNION ALL SELECT streak FROM pvp_history WHERE user_no=@userNumber) s"));
        var clearedAreas = session.Query<AchievementProgressRow>(
            "SELECT r.type+40000 AS Type,r.sub_type AS SubType,1 AS Count FROM master_achievement r"
            + " WHERE r.type BETWEEN 1001 AND 2025 AND EXISTS(SELECT 1 FROM master_stage_area a WHERE a.area_id=r.sub_type)"
            + " AND NOT EXISTS(SELECT 1 FROM master_stage_area a WHERE a.area_id=r.sub_type AND NOT EXISTS(SELECT 1 FROM stage_clear c"
            + " WHERE c.user_no=@userNumber AND c.stage_id=a.stage_id AND c.stage_mode=CASE WHEN r.type<2000 THEN 1 ELSE 2 END AND c.clear_count>0))",
            new { userNumber });
        foreach (var row in clearedAreas) RecordProgress(userNumber, row.Type, row.SubType, row.Count);
    }

    /// <summary>Stores a reported total; a lower total never replaces a higher one.</summary>
    public void RecordProgress(long userNumber, long type, long subType, long count)
    {
        session.Execute(
            "UPDATE achievement_progress SET count=@count WHERE user_no=@userNumber AND type=@type AND sub_type=@subType AND count<@count",
            new { userNumber, type, subType, count });
        session.Execute(
            $"{session.Dialect.InsertOrIgnore} INTO achievement_progress(user_no, type, sub_type, count) VALUES(@userNumber, @type, @subType, @count)",
            new { userNumber, type, subType, count });
    }
}
