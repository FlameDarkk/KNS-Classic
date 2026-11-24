namespace KnS.Data.Repositories;

/// <summary>A player's arena record (pvp_stat).</summary>
public sealed record PvpStatRecord
{
    public long UserNumber { get; init; }
    public long Points { get; init; }
    public long Battles { get; init; }
    public long Wins { get; init; }
    public long Streak { get; init; }
    public long RefreshUntil { get; init; }
    public long RefreshSeed { get; init; }
}

public sealed record PvpPlayRecord
{
    public long OpponentNumber { get; init; }
    public long Result { get; init; }
    public long Complete { get; init; }
}

public sealed class PvpRepository(DataSession session)
{
    public PvpControlRecord Control(long now, long duration)
    {
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO pvp_control(id,generation,season_no,ends_at,closed) VALUES(1,1,1,@end,0)", new { end = now + duration });
        session.Execute("UPDATE pvp_control SET generation=generation WHERE id=1");
        return session.QueryFirstOrDefault<PvpControlRecord>("SELECT generation AS Generation,season_no AS SeasonNumber,ends_at AS EndsAt,closed AS Closed FROM pvp_control WHERE id=1")!;
    }

    public void SetControl(PvpControlRecord control) => session.Execute(
        "UPDATE pvp_control SET generation=@Generation,season_no=@SeasonNumber,ends_at=@EndsAt,closed=@Closed WHERE id=1", control);

    public void Archive(long generation)
    {
        session.Execute("INSERT INTO pvp_history(generation,user_no,points,battles,wins,streak) SELECT @generation,user_no,points,battles,wins,streak FROM pvp_stat", new { generation });
        session.Execute("DELETE FROM pvp_stat");
        session.Execute("DELETE FROM pvp_opponent_result");
        session.Execute("DELETE FROM pvp_opponent_list");
    }

    /// <summary>The opponents currently listed for this player, in display order.</summary>
    public IReadOnlyList<long> OpponentList(long userNumber) => session.Query<long>(
        "SELECT opponent_no FROM pvp_opponent_list WHERE user_no=@userNumber ORDER BY slot", new { userNumber });

    public void SetOpponentList(long userNumber, IReadOnlyList<long> opponents)
    {
        session.Execute("DELETE FROM pvp_opponent_list WHERE user_no=@userNumber", new { userNumber });
        for (int slot = 0; slot < opponents.Count; slot++)
        {
            session.Execute("INSERT INTO pvp_opponent_list(user_no,slot,opponent_no) VALUES(@userNumber,@slot,@opponent)",
                new { userNumber, slot, opponent = opponents[slot] });
        }
    }

    public void SetOpponentResult(long userNumber, long opponentNumber, long result) => session.Execute(
        $"{session.Dialect.InsertOrReplace} INTO pvp_opponent_result(user_no,opponent_no,result) VALUES(@userNumber,@opponentNumber,@result)",
        new { userNumber, opponentNumber, result });

    public long OpponentResult(long userNumber, long opponentNumber) => session.QueryFirstOrDefault<long?>(
        "SELECT result FROM pvp_opponent_result WHERE user_no=@userNumber AND opponent_no=@opponentNumber", new { userNumber, opponentNumber }) ?? 0;

    public void ClearOpponentResults(long userNumber) =>
        session.Execute("DELETE FROM pvp_opponent_result WHERE user_no=@userNumber", new { userNumber });

    /// <summary>Marks an Arena score mission paid; false when this season already paid it.</summary>
    public bool TryMarkMission(long generation, long userNumber, long grade) => session.Execute(
        $"{session.Dialect.InsertOrIgnore} INTO pvp_mission_paid(generation,user_no,grade) VALUES(@generation,@userNumber,@grade)",
        new { generation, userNumber, grade }) == 1;

    /// <summary>The season's final standings: players with at least 1 score, best first (same order as <see cref="GetRanking"/>).</summary>
    public IReadOnlyList<PvpStatRecord> Participants() => session.Query<PvpStatRecord>(
        $"SELECT {Columns} FROM pvp_stat WHERE points>0 ORDER BY points DESC, user_no");

    /// <summary>Records a season ranking reward; false when this season already paid this player.</summary>
    public bool TryMarkRankingPaid(long generation, long userNumber, long grade, long rank, long total) => session.Execute(
        $"{session.Dialect.InsertOrIgnore} INTO pvp_rank_paid(generation,user_no,grade,ranking,total) VALUES(@generation,@userNumber,@grade,@rank,@total)",
        new { generation, userNumber, grade, rank, total }) == 1;

    /// <summary>The player's most recent paid season ranking reward, or null.</summary>
    public PvpRankPaidRecord? LastRankingPaid(long userNumber) => session.QueryFirstOrDefault<PvpRankPaidRecord>(
        "SELECT generation AS Generation, grade AS Grade, ranking AS Rank, total AS Total FROM pvp_rank_paid"
        + " WHERE user_no=@userNumber ORDER BY generation DESC LIMIT 1", new { userNumber });

    public void CaptureSeason(long userNumber, long generation) => session.Execute(
        $"{session.Dialect.InsertOrReplace} INTO pvp_attempt_season(user_no,generation) VALUES(@userNumber,@generation)", new { userNumber, generation });

    public long? AttemptSeason(long userNumber) => session.QueryFirstOrDefault<long?>(
        "SELECT generation FROM pvp_attempt_season WHERE user_no=@userNumber", new { userNumber });

    public void RecordHistoricalBattle(long generation, long userNumber, long points, bool won)
    {
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO pvp_history(generation,user_no) VALUES(@generation,@userNumber)", new { generation, userNumber });
        session.Execute("UPDATE pvp_history SET points=points+@points,battles=battles+1,wins=wins+@win,streak=CASE WHEN @win=1 THEN streak+1 ELSE 0 END WHERE generation=@generation AND user_no=@userNumber",
            new { generation, userNumber, points, win = won ? 1 : 0 });
    }

    public void BeginPlay(long userNumber, long opponentNumber) =>
        session.Execute($"{session.Dialect.InsertOrReplace} INTO pvp_play(user_no,opponent_no,result,complete) VALUES(@userNumber,@opponentNumber,0,0)",
            new { userNumber, opponentNumber });

    public PvpPlayRecord? GetPlay(long userNumber) =>
        session.QueryFirstOrDefault<PvpPlayRecord>(
            "SELECT opponent_no AS OpponentNumber,result AS Result,complete AS Complete FROM pvp_play WHERE user_no=@userNumber", new { userNumber });

    public void CompletePlay(long userNumber, long result) =>
        session.Execute("UPDATE pvp_play SET result=@result,complete=1 WHERE user_no=@userNumber", new { userNumber, result });

    private const string Columns = "user_no AS UserNumber, points AS Points, battles AS Battles, wins AS Wins, streak AS Streak, refresh_until AS RefreshUntil, refresh_seed AS RefreshSeed";

    public PvpStatRecord Get(long userNumber) =>
        session.QueryFirstOrDefault<PvpStatRecord>($"SELECT {Columns} FROM pvp_stat WHERE user_no=@userNumber", new { userNumber })
        ?? new PvpStatRecord { UserNumber = userNumber };

    public void ApplyRefresh(long userNumber, long refreshUntil, bool resetStreak)
    {
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO pvp_stat(user_no) VALUES(@userNumber)", new { userNumber });
        session.Execute(
            "UPDATE pvp_stat SET refresh_until=@refreshUntil, refresh_seed=refresh_seed+1, streak=CASE WHEN @resetStreak=1 THEN 0 ELSE streak END WHERE user_no=@userNumber",
            new { userNumber, refreshUntil, resetStreak = resetStreak ? 1 : 0 });
    }

    public void RecordBattle(long userNumber, long points, bool won)
    {
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO pvp_stat(user_no) VALUES(@userNumber)", new { userNumber });
        session.Execute(
            "UPDATE pvp_stat SET points=points+@points, battles=battles+1, wins=wins+@win, streak=CASE WHEN @win=1 THEN streak+1 ELSE 0 END WHERE user_no=@userNumber",
            new { userNumber, points, win = won ? 1 : 0 });
        new AchievementRepository(session).RecordProgress(userNumber, 40082, 0, Get(userNumber).Streak);
    }

    /// <summary>Players by points, best first; players without a record count as 0 points.</summary>
    public IReadOnlyList<PvpStatRecord> GetRanking(int limit) =>
        session.Query<PvpStatRecord>(
            "SELECT g.user_no AS UserNumber, COALESCE(p.points, 0) AS Points, COALESCE(p.battles, 0) AS Battles,"
            + " COALESCE(p.wins, 0) AS Wins, COALESCE(p.streak, 0) AS Streak"
            + " FROM gameuser g LEFT JOIN pvp_stat p ON p.user_no=g.user_no WHERE g.user_no>0"
            + " ORDER BY COALESCE(p.points, 0) DESC, g.user_no LIMIT @limit",
            new { limit });
}

public sealed record PvpRankPaidRecord
{
    public long Generation { get; init; }
    public long Grade { get; init; }
    public long Rank { get; init; }
    public long Total { get; init; }
}

public sealed record PvpControlRecord
{
    public long Generation { get; init; }
    public long SeasonNumber { get; init; }
    public long EndsAt { get; init; }
    public long Closed { get; init; }
}
