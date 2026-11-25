namespace KnS.Data.Repositories;

/// <summary>Friend links (friend_link). Status 1 = requested, 2 = friends; friends are stored once per direction.</summary>
public sealed class FriendRepository(DataSession session)
{
    public const long Requested = 1;
    public const long Accepted = 2;

    public IReadOnlyList<long> GetFriends(long userNumber) =>
        session.Query<long>(
            "SELECT friend_no FROM friend_link WHERE user_no=@userNumber AND status=@status ORDER BY friend_no",
            new { userNumber, status = Accepted });

    /// <summary>People who asked this player and wait for an answer.</summary>
    public IReadOnlyList<long> GetIncoming(long userNumber) =>
        session.Query<long>(
            "SELECT user_no FROM friend_link WHERE friend_no=@userNumber AND status=@status ORDER BY created",
            new { userNumber, status = Requested });

    /// <summary>People this player asked who have not answered.</summary>
    public IReadOnlyList<long> GetOutgoing(long userNumber) =>
        session.Query<long>(
            "SELECT friend_no FROM friend_link WHERE user_no=@userNumber AND status=@status ORDER BY created",
            new { userNumber, status = Requested });

    public long? GetStatus(long userNumber, long friendNumber) =>
        session.QueryFirstOrDefault<long?>(
            "SELECT status FROM friend_link WHERE user_no=@userNumber AND friend_no=@friendNumber",
            new { userNumber, friendNumber });

    public int CountFriends(long userNumber) =>
        (int)session.QueryFirstOrDefault<long>(
            "SELECT COUNT(*) FROM friend_link WHERE user_no=@userNumber AND status=@status",
            new { userNumber, status = Accepted });

    public void Request(long userNumber, long friendNumber, long now) =>
        session.Execute(
            $"{session.Dialect.InsertOrReplace} INTO friend_link(user_no, friend_no, status, created) VALUES(@userNumber, @friendNumber, @status, @now)",
            new { userNumber, friendNumber, status = Requested, now });

    /// <summary>Turns the request from <paramref name="requester"/> to <paramref name="accepter"/> into a friendship.</summary>
    public void Accept(long requester, long accepter, long now)
    {
        session.Execute("DELETE FROM friend_link WHERE user_no=@requester AND friend_no=@accepter",
            new { requester, accepter });
        foreach ((long from, long to) in new[] { (requester, accepter), (accepter, requester) })
        {
            session.Execute(
                $"{session.Dialect.InsertOrReplace} INTO friend_link(user_no, friend_no, status, created) VALUES(@from, @to, @status, @now)",
                new { from, to, status = Accepted, now });
        }
    }

    /// <summary>Removes any link between the two (friendship, or a request either way).</summary>
    public void Remove(long userNumber, long friendNumber) =>
        session.Execute(
            "DELETE FROM friend_link WHERE (user_no=@userNumber AND friend_no=@friendNumber) OR (user_no=@friendNumber AND friend_no=@userNumber)",
            new { userNumber, friendNumber });

    public void SetLastSent(long userNumber, long friendNumber, long now) =>
        session.Execute("UPDATE friend_link SET last_sent=@now WHERE user_no=@userNumber AND friend_no=@friendNumber",
            new { userNumber, friendNumber, now });

    public int CountSentSince(long userNumber, long since) =>
        (int)session.QueryFirstOrDefault<long>(
            "SELECT COUNT(*) FROM friend_link WHERE user_no=@userNumber AND status=@status AND last_sent>=@since",
            new { userNumber, status = Accepted, since });

    public long GetLastSent(long userNumber, long friendNumber) =>
        session.QueryFirstOrDefault<long?>(
            "SELECT last_sent FROM friend_link WHERE user_no=@userNumber AND friend_no=@friendNumber",
            new { userNumber, friendNumber }) ?? 0;

    public long GetLastHelp(long userNumber, long friendNumber) =>
        session.QueryFirstOrDefault<long?>(
            "SELECT last_help FROM friend_link WHERE user_no=@userNumber AND friend_no=@friendNumber",
            new { userNumber, friendNumber }) ?? 0;

    public void SetLastHelp(long userNumber, long friendNumber, long now) =>
        session.Execute("UPDATE friend_link SET last_help=@now WHERE user_no=@userNumber AND friend_no=@friendNumber",
            new { userNumber, friendNumber, now });

    public (int Requests, int Deletes, int SentFp, int RecvFp) GetDailyLimits(long userNumber, long day)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = session.QueryRows(
            "SELECT requests, deletes, sent_fp, recv_fp FROM friend_daily_limit WHERE user_no=@userNumber AND day_no=@day",
            new { userNumber, day });
        if (rows.Count == 0) return (0, 0, 0, 0);
        return (Convert.ToInt32(rows[0]["requests"]), Convert.ToInt32(rows[0]["deletes"]),
            Convert.ToInt32(rows[0]["sent_fp"]), Convert.ToInt32(rows[0]["recv_fp"]));
    }

    public void AddDailyUsage(long userNumber, long day, int requests = 0, int deletes = 0, int sentFp = 0, int recvFp = 0)
    {
        session.Execute(
            $"{session.Dialect.InsertOrIgnore} INTO friend_daily_limit(user_no, day_no, requests, deletes, sent_fp, recv_fp) VALUES(@userNumber, @day, 0, 0, 0, 0)",
            new { userNumber, day });
        session.Execute(
            "UPDATE friend_daily_limit SET requests=requests+@requests, deletes=deletes+@deletes, sent_fp=sent_fp+@sentFp, recv_fp=recv_fp+@recvFp WHERE user_no=@userNumber AND day_no=@day",
            new { userNumber, day, requests, deletes, sentFp, recvFp });
    }
}
