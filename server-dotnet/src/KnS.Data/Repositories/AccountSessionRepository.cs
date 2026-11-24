namespace KnS.Data.Repositories;

/// <summary>Sign-in tokens of id + password accounts (account_session). Only a SHA-256 of each token is stored.</summary>
public sealed class AccountSessionRepository(DataSession session)
{
    public void Add(string tokenHash, string loginId, double created, double expires) =>
        session.Execute(
            "INSERT INTO account_session(token_hash, login_id, created, expires) VALUES(@tokenHash, @loginId, @created, @expires)",
            new { tokenHash, loginId, created, expires });

    /// <summary>The login id of a token that has not expired at <paramref name="now"/>.</summary>
    public string? FindLogin(string tokenHash, double now) =>
        session.QueryFirstOrDefault<string>(
            "SELECT login_id FROM account_session WHERE token_hash=@tokenHash AND expires>@now", new { tokenHash, now });

    public void Extend(string tokenHash, double expires) =>
        session.Execute("UPDATE account_session SET expires=@expires WHERE token_hash=@tokenHash", new { tokenHash, expires });

    public void Remove(string tokenHash) =>
        session.Execute("DELETE FROM account_session WHERE token_hash=@tokenHash", new { tokenHash });

    /// <summary>Signs the account out everywhere except the token kept (null keeps none).</summary>
    public void RemoveOthers(string loginId, string? keptHash) =>
        session.Execute("DELETE FROM account_session WHERE login_id=@loginId AND token_hash<>@keptHash", new { loginId, keptHash = keptHash ?? "" });

    public void RemoveExpired(double now) =>
        session.Execute("DELETE FROM account_session WHERE expires<=@now", new { now });
}
