namespace KnS.Data.Repositories;

public sealed record LoginAccountRecord
{
    public string LoginId { get; init; } = "";
    public string PasswordHash { get; init; } = "";

    /// <summary>The device id the game logs in with: it is what ties this account to its player.</summary>
    public string DeviceId { get; init; } = "";

    public string GuestUid { get; init; } = "";

    /// <summary>The display username chosen at register; empty for accounts registered before usernames existed.</summary>
    public string Nickname { get; init; } = "";
}

/// <summary>Id and password accounts of the browser build (login_account).</summary>
public sealed class LoginAccountRepository(DataSession session)
{
    private const string Columns = "login_id AS LoginId, password_hash AS PasswordHash, did AS DeviceId, guest_uid AS GuestUid, nickname AS Nickname";

    public LoginAccountRecord? Find(string loginId) =>
        session.QueryFirstOrDefault<LoginAccountRecord>($"SELECT {Columns} FROM login_account WHERE login_id=@loginId", new { loginId });

    public bool IsDeviceTaken(string deviceId) =>
        session.Exists("SELECT 1 FROM login_account WHERE did=@deviceId", new { deviceId });

    /// <summary>The account bound to a device id, if one registered it.</summary>
    public LoginAccountRecord? FindByDevice(string deviceId) =>
        session.QueryFirstOrDefault<LoginAccountRecord>($"SELECT {Columns} FROM login_account WHERE did=@deviceId", new { deviceId });

    /// <summary>True when another account already holds this username (case-insensitive); empty names never count.</summary>
    public bool IsNicknameTaken(string nickname) =>
        nickname.Length > 0
        && session.Exists("SELECT 1 FROM login_account WHERE LOWER(nickname)=LOWER(@nickname)", new { nickname });

    /// <summary>True when a different account already holds this username (case-insensitive); empty names never count.</summary>
    public bool IsNicknameTakenByOther(string nickname, string excludeLoginId) =>
        nickname.Length > 0
        && session.Exists(
            "SELECT 1 FROM login_account WHERE login_id<>@excludeLoginId AND LOWER(nickname)=LOWER(@nickname)",
            new { nickname, excludeLoginId });

    public void SetNickname(string loginId, string nickname) =>
        session.Execute("UPDATE login_account SET nickname=@nickname WHERE login_id=@loginId", new { loginId, nickname });

    /// <summary>Returns false when the id is already taken.</summary>
    public bool TryAdd(string loginId, string passwordHash, string deviceId, string guestUid, string nickname, double createdAt) =>
        session.Execute(
            $"{session.Dialect.InsertOrIgnore} INTO login_account(login_id, password_hash, did, guest_uid, nickname, created)"
            + " VALUES(@loginId, @passwordHash, @deviceId, @guestUid, @nickname, @createdAt)",
            new { loginId, passwordHash, deviceId, guestUid, nickname, createdAt }) > 0;

    public void SetPasswordHash(string loginId, string passwordHash) =>
        session.Execute("UPDATE login_account SET password_hash=@passwordHash WHERE login_id=@loginId", new { loginId, passwordHash });
}
