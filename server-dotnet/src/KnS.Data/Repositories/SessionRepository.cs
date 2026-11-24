namespace KnS.Data.Repositories;

/// <summary>Gateway login sessions (player_session). Only hashes are stored, so a copy of the database cannot act as a player.</summary>
public sealed class SessionRepository(DataSession session)
{
    public void Add(string sessionHash, long userNumber, double createdAt) =>
        session.Execute(
            "INSERT INTO player_session(session_hash, user_no, created) VALUES(@sessionHash, @userNumber, @createdAt)",
            new { sessionHash, userNumber, createdAt });

    public bool Exists(string sessionHash, long userNumber) =>
        session.Exists(
            "SELECT 1 FROM player_session WHERE session_hash=@sessionHash AND user_no=@userNumber",
            new { sessionHash, userNumber });

    /// <summary>Drops all but the player's newest sessions.</summary>
    public void KeepNewest(long userNumber, int count)
    {
        IReadOnlyList<string> old = session.Query<string>(
            "SELECT session_hash FROM player_session WHERE user_no=@userNumber ORDER BY created DESC",
            new { userNumber }).Skip(count).ToList();
        foreach (string sessionHash in old)
        {
            session.Execute("DELETE FROM player_session WHERE session_hash=@sessionHash", new { sessionHash });
        }
    }
}
