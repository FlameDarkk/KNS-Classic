namespace KnS.Data.Repositories;

/// <summary>Someone allowed to play, known by email; the game never sends it, so a device id is attached to it.</summary>
public sealed record TesterRecord
{
    public string Email { get; init; } = "";

    /// <summary>The attached device (hub_did); empty until the tester has tried to log in and the owner attaches it.</summary>
    public string DeviceId { get; init; } = "";

    public string Note { get; init; } = "";

    public bool Revoked { get; init; }
}

/// <summary>A device the allowlist turned away, newest first, for the owner to attach to an email.</summary>
public sealed record RefusedDeviceRecord
{
    public string DeviceId { get; init; } = "";
    public long UserNumber { get; init; }
    public long Attempts { get; init; }
    public double LastSeen { get; init; }
}

/// <summary>The tester allowlist (tester) and the devices it refused (access_attempt).</summary>
public sealed class TesterRepository(DataSession session)
{
    /// <summary>Refused devices kept; older ones go, so strangers cannot grow the table.</summary>
    private const int RefusedDevicesKept = 200;

    private const string TesterColumns = "email AS Email, device_id AS DeviceId, note AS Note, revoked AS Revoked";

    /// <summary>Whether the device is attached to a tester whose access is not revoked.</summary>
    public bool IsActiveDevice(string deviceId) =>
        deviceId.Length > 0
        && session.Exists("SELECT 1 FROM tester WHERE device_id=@deviceId AND revoked=0 LIMIT 1", new { deviceId });

    public IReadOnlyList<TesterRecord> List() =>
        session.Query<TesterRecord>($"SELECT {TesterColumns} FROM tester ORDER BY email");

    public TesterRecord? Find(string email) =>
        session.QueryFirstOrDefault<TesterRecord>($"SELECT {TesterColumns} FROM tester WHERE email=@email", new { email });

    public TesterRecord? FindByDevice(string deviceId) =>
        deviceId.Length == 0
            ? null
            : session.QueryFirstOrDefault<TesterRecord>($"SELECT {TesterColumns} FROM tester WHERE device_id=@deviceId", new { deviceId });

    public void Add(string email, string note, double createdAt) =>
        session.Execute(
            "INSERT INTO tester(email, device_id, note, revoked, created) VALUES(@email, '', @note, 0, @createdAt)",
            new { email, note, createdAt });

    public void SetDevice(string email, string deviceId) =>
        session.Execute("UPDATE tester SET device_id=@deviceId WHERE email=@email", new { email, deviceId });

    public void SetNote(string email, string note) =>
        session.Execute("UPDATE tester SET note=@note WHERE email=@email", new { email, note });

    public void SetRevoked(string email, bool revoked) =>
        session.Execute("UPDATE tester SET revoked=@revoked WHERE email=@email", new { email, revoked = revoked ? 1 : 0 });

    /// <summary>Notes a refusal (once per device, counting repeats), then trims the table.</summary>
    public void RecordRefusal(string deviceId, long userNumber, double now)
    {
        session.Execute(
            "INSERT INTO access_attempt(device_id, user_no, attempts, last_seen) VALUES(@deviceId, @userNumber, 1, @now)"
            + $" {session.Dialect.OnConflictUpdate("device_id")} attempts=attempts+1, last_seen=@now, user_no=@userNumber",
            new { deviceId, userNumber, now });
        session.Execute(
            "DELETE FROM access_attempt WHERE device_id NOT IN"
            + $" (SELECT device_id FROM (SELECT device_id FROM access_attempt ORDER BY last_seen DESC LIMIT {RefusedDevicesKept}) newest)");
    }

    /// <summary>Refused devices not yet attached to a tester, newest first.</summary>
    public IReadOnlyList<RefusedDeviceRecord> ListRefused() =>
        session.Query<RefusedDeviceRecord>(
            "SELECT device_id AS DeviceId, user_no AS UserNumber, attempts AS Attempts, last_seen AS LastSeen FROM access_attempt"
            + " WHERE device_id NOT IN (SELECT device_id FROM tester WHERE device_id<>'') ORDER BY last_seen DESC");
}
