namespace KnS.Data.Repositories;

/// <summary>A player's result against one raid boss (raid_score).</summary>
public sealed record RaidScoreRecord
{
    public long UserNumber { get; init; }
    public long Score { get; init; }
    public long Plays { get; init; }
}

public sealed record RaidPlayRecord
{
    public long RaidNumber { get; init; }
    public long Score { get; init; }
    public long Complete { get; init; }
}

public sealed record RaidControlRecord
{
    public long RaidNumber { get; init; }
    public long EndsAt { get; init; }
}

public sealed class RaidRepository(DataSession session)
{
    public long DailyEntries(long userNumber, long day)
    {
        // Preserve known starts from before this counter existed. Quest progress freezes on claim,
        // so this is a lower bound for legacy data, never a reason to reuse the weekly play total.
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO raid_daily_entry(user_no,day_no,entries)"
            + " SELECT @userNumber,@day,COALESCE((SELECT quest_count FROM daily_quest_progress"
            + " WHERE user_no=@userNumber AND day_no=@day AND quest_id=5300003),0)", new { userNumber, day });
        return session.QueryFirstOrDefault<long>(
            "SELECT entries FROM raid_daily_entry WHERE user_no=@userNumber AND day_no=@day", new { userNumber, day });
    }

    public bool TryUseDailyEntry(long userNumber, long day, long limit)
    {
        DailyEntries(userNumber, day);
        return session.Execute("UPDATE raid_daily_entry SET entries=entries+1"
            + " WHERE user_no=@userNumber AND day_no=@day AND entries<@limit", new { userNumber, day, limit }) == 1;
    }

    public long? LatestUnclaimedRaid(long userNumber, long currentRaid) => session.QueryFirstOrDefault<long?>(
        "SELECT MAX(s.raid_no) FROM raid_score s WHERE s.user_no=@userNumber AND s.raid_no>0 AND s.raid_no<@currentRaid AND s.plays>0"
        + " AND NOT EXISTS(SELECT 1 FROM raid_rank_claim c WHERE c.user_no=s.user_no AND c.raid_no=s.raid_no)", new { userNumber, currentRaid });

    public bool ClaimRank(long userNumber, long raidNumber) => session.Execute(
        $"{session.Dialect.InsertOrIgnore} INTO raid_rank_claim(raid_no,user_no) VALUES(@raidNumber,@userNumber)", new { userNumber, raidNumber }) == 1;

    public RaidRankSnapshot? FrozenRank(long userNumber, long raidNumber)
    {
        if (session.Execute($"{session.Dialect.InsertOrIgnore} INTO raid_rank_settlement(raid_no) VALUES(@raidNumber)", new { raidNumber }) == 1)
        {
            IReadOnlyList<RaidScoreRecord> ranking = GetRanking(raidNumber, int.MaxValue).Where(row => row.Plays > 0).ToList();
            for (int i = 0; i < ranking.Count; i++)
            {
                session.Execute("INSERT INTO raid_rank_snapshot(raid_no,user_no,rank_no,total_users) VALUES(@raidNumber,@userNumber,@rank,@total)",
                    new { raidNumber, userNumber = ranking[i].UserNumber, rank = i + 1, total = ranking.Count });
            }
        }
        return session.QueryFirstOrDefault<RaidRankSnapshot>(
            "SELECT rank_no AS Rank, total_users AS Total FROM raid_rank_snapshot WHERE raid_no=@raidNumber AND user_no=@userNumber",
            new { raidNumber, userNumber });
    }

    public IReadOnlyList<RaidRankRewardRule> RankRewards() => session.Query<RaidRankRewardRule>(
        "SELECT grade AS Grade, reward1_item AS Reward1Item, reward1_value AS Reward1Value, reward2_item AS Reward2Item,"
        + " reward2_value AS Reward2Value, text_index AS TextIndex, rank_min AS RankMin, rank_max AS RankMax,"
        + " percent_max AS PercentMax FROM master_raid_rank_reward ORDER BY grade");

    public IReadOnlyList<RaidChallengeRule> ChallengeRules() => session.Query<RaidChallengeRule>(
        "SELECT challenge_id AS ChallengeId, raid_id AS RaidId, threshold AS Threshold,"
        + " reward_item_id AS RewardItemId, reward_count AS RewardCount FROM master_raid_challenge ORDER BY challenge_id");

    public IReadOnlyList<RaidChallengeProgress> Challenges(long userNumber) => session.Query<RaidChallengeProgress>(
        "SELECT raid_id AS RaidId, score AS Count FROM raid_challenge_progress WHERE user_no=@userNumber ORDER BY raid_id", new { userNumber });

    public IReadOnlyList<RaidPlayRecord> HistoricalScores(long userNumber) => session.Query<RaidPlayRecord>(
        "SELECT raid_no AS RaidNumber, score AS Score FROM raid_score WHERE user_no=@userNumber", new { userNumber });

    public IReadOnlyList<long> ChallengeClaims(long userNumber) => session.Query<long>(
        "SELECT challenge_id FROM raid_challenge_claim WHERE user_no=@userNumber ORDER BY challenge_id", new { userNumber });

    public bool ClaimChallenge(long userNumber, long challengeId) => session.Execute(
        $"{session.Dialect.InsertOrIgnore} INTO raid_challenge_claim(user_no,challenge_id) VALUES(@userNumber,@challengeId)",
        new { userNumber, challengeId }) == 1;

    public void RecordChallengeScore(long userNumber, long raidId, long score)
    {
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO raid_challenge_progress(user_no,raid_id) VALUES(@userNumber,@raidId)", new { userNumber, raidId });
        session.Execute("UPDATE raid_challenge_progress SET score=CASE WHEN @score>score THEN @score ELSE score END"
            + " WHERE user_no=@userNumber AND raid_id=@raidId", new { userNumber, raidId, score });
    }

    public RaidControlRecord? GetControl() => session.QueryFirstOrDefault<RaidControlRecord>(
        "SELECT raid_no AS RaidNumber, ends_at AS EndsAt FROM raid_control WHERE id=1");

    public void LockControl() => session.Execute("UPDATE raid_control SET raid_no=raid_no WHERE id=1");

    public void SetControl(long raidNumber, long endsAt)
    {
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO raid_control(id,raid_no,ends_at) VALUES(1,@raidNumber,@endsAt)", new { raidNumber, endsAt });
        session.Execute("UPDATE raid_control SET raid_no=@raidNumber,ends_at=@endsAt WHERE id=1", new { raidNumber, endsAt });
    }

    public void BeginPlay(long userNumber, long raidNumber) =>
        session.Execute($"{session.Dialect.InsertOrReplace} INTO raid_play(user_no, raid_no, score, complete) VALUES(@userNumber,@raidNumber,0,0)",
            new { userNumber, raidNumber });

    public RaidPlayRecord? GetPlay(long userNumber) =>
        session.QueryFirstOrDefault<RaidPlayRecord>(
            "SELECT raid_no AS RaidNumber, score AS Score, complete AS Complete FROM raid_play WHERE user_no=@userNumber", new { userNumber });

    public void CompletePlay(long userNumber, long score) =>
        session.Execute("UPDATE raid_play SET score=@score, complete=1 WHERE user_no=@userNumber", new { userNumber, score });

    public RaidScoreRecord Get(long raidNumber, long userNumber) =>
        session.QueryFirstOrDefault<RaidScoreRecord>(
            "SELECT user_no AS UserNumber, score AS Score, plays AS Plays FROM raid_score WHERE raid_no=@raidNumber AND user_no=@userNumber",
            new { raidNumber, userNumber }) ?? new RaidScoreRecord { UserNumber = userNumber };

    /// <summary>Adds a play; the player's score is the best single play.</summary>
    public void RecordPlay(long raidNumber, long userNumber, long score)
    {
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO raid_score(raid_no, user_no) VALUES(@raidNumber, @userNumber)", new { raidNumber, userNumber });
        session.Execute(
            "UPDATE raid_score SET plays=plays+1, score=CASE WHEN @score>score THEN @score ELSE score END WHERE raid_no=@raidNumber AND user_no=@userNumber",
            new { raidNumber, userNumber, score });
    }

    /// <summary>Total damage dealt to the boss: every play's score is subtracted from its health, so the sum of best scores is used.</summary>
    public long GetTotalScore(long raidNumber) =>
        session.QueryFirstOrDefault<long?>("SELECT SUM(score) FROM raid_score WHERE raid_no=@raidNumber", new { raidNumber }) ?? 0;

    public IReadOnlyList<RaidScoreRecord> GetRanking(long raidNumber, int limit) =>
        session.Query<RaidScoreRecord>(
            "SELECT user_no AS UserNumber, score AS Score, plays AS Plays FROM raid_score WHERE raid_no=@raidNumber ORDER BY score DESC, user_no LIMIT @limit",
            new { raidNumber, limit });
}

public sealed record RaidChallengeRule
{
    public long ChallengeId { get; init; }
    public long RaidId { get; init; }
    public long Threshold { get; init; }
    public long RewardItemId { get; init; }
    public long RewardCount { get; init; }
}

public sealed record RaidChallengeProgress
{
    public long RaidId { get; init; }
    public long Count { get; init; }
}

public sealed record RaidRankSnapshot
{
    public long Rank { get; init; }
    public long Total { get; init; }
}

public sealed record RaidRankRewardRule
{
    public long Grade { get; init; }
    public long Reward1Item { get; init; }
    public long Reward1Value { get; init; }
    public long Reward2Item { get; init; }
    public long Reward2Value { get; init; }
    public long TextIndex { get; init; }
    public long RankMin { get; init; }
    public long RankMax { get; init; }
    public long PercentMax { get; init; }
}
