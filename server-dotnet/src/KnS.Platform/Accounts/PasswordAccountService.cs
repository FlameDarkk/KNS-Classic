using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KnS.Data.Repositories;
using KnS.Platform.Identifiers;

namespace KnS.Platform.Accounts;

public enum AccountFailure
{
    None,
    InvalidId,
    InvalidPassword,
    InvalidNickname,
    NicknameTaken,
    IdTaken,
    DeviceTaken,
    WrongCredentials,
    TooManyAttempts,
}

/// <summary>What the page stores after a register or login: the game then logs in with this device id.</summary>
public sealed record AccountSession(string LoginId, string DeviceId, string GuestUid);

public sealed record AccountResult(AccountSession? Session, AccountFailure Failure);

/// <summary>
/// Failed logins per id, shared across requests. Five wrong passwords lock the id for a minute. Kept in memory: a restart
/// clears it, which is fine for a local server.
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    private const int MaxFailures = 5;
    private const int PruneThreshold = 1024;
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset LastFailure)> _entries = new();

    public bool IsLocked(string loginId) =>
        _entries.TryGetValue(loginId, out var entry) && entry.Failures >= MaxFailures && clock.GetUtcNow() - entry.LastFailure < LockTime;

    public void RecordFailure(string loginId)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (_entries.Count >= PruneThreshold)
        {
            foreach (KeyValuePair<string, (int Failures, DateTimeOffset LastFailure)> entry in _entries)
            {
                if (now - entry.Value.LastFailure >= LockTime)
                {
                    _entries.TryRemove(entry.Key, out _);
                }
            }
        }

        _entries.AddOrUpdate(loginId, (1, now), (_, entry) => (now - entry.LastFailure >= LockTime ? 1 : entry.Failures + 1, now));
    }

    public void Clear(string loginId) => _entries.TryRemove(loginId, out _);
}

/// <summary>
/// Registers and checks id + password accounts (login_account). An account points at a device id; a guest who registers keeps their
/// player because the account takes over the guest's device id. Passwords are salted PBKDF2-SHA256 and are never logged. Both
/// standard base64 and URL-safe unpadded base64 PBKDF2-SHA256 hashes verify with their stored iteration count.
/// </summary>
public sealed partial class PasswordAccountService(
    LoginAccountRepository accounts,
    PlayerRepository players,
    LoginThrottle throttle,
    PlatformIdentifiers identifiers)
{
    private const int Iterations = 200_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MinPasswordLength = 8;
    private const int MaxPasswordLength = 128;

    /// <summary>Verified against when the id is unknown, so an unknown id takes as long as a wrong password.</summary>
    private static readonly string DecoyHash = HashPassword("decoy password");

    public AccountResult Register(string? rawId, string? password, string? deviceId, string? guestUid, string? nickname = null)
    {
        string loginId = Normalize(rawId);
        if (!IdPattern().IsMatch(loginId))
        {
            return Failed(AccountFailure.InvalidId);
        }

        if (!IsValidPassword(password))
        {
            return Failed(AccountFailure.InvalidPassword);
        }

        string displayName = (nickname ?? "").Trim();
        if (!IsValidNickname(displayName))
        {
            return Failed(AccountFailure.InvalidNickname);
        }

        // Pre-check only: no unique index can enforce this because empty nicknames repeat by design. Both stores are checked: a new
        // account's username waits in login_account until the game creates its player, while existing players show gameuser.nickname.
        if (players.IsNicknameTaken(displayName) || accounts.IsNicknameTaken(displayName))
        {
            return Failed(AccountFailure.NicknameTaken);
        }

        bool keepsGuest = deviceId is not null && DevicePattern().IsMatch(deviceId);
        if (keepsGuest && accounts.IsDeviceTaken(deviceId!))
        {
            return Failed(AccountFailure.DeviceTaken);
        }

        string device = keepsGuest ? deviceId! : "web-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        string guest = guestUid is not null && GuestPattern().IsMatch(guestUid) ? guestUid : NewGuestUid();
        if (!accounts.TryAdd(loginId, HashPassword(password!), device, guest, displayName, identifiers.UnixTime))
        {
            return Failed(AccountFailure.IdTaken);
        }

        // A guest keeping their player gets the username right away; a new device's player adopts it when the game creates it.
        if ((players.FindByDevice(device) ?? players.FindByLinkedDevice(device)) is { } existing)
        {
            players.SetNickname(existing.UserNumber, displayName);
        }

        return new AccountResult(new AccountSession(loginId, device, guest), AccountFailure.None);
    }

    /// <summary>The account of a signed-in id, without a password check (the caller verified a session token).</summary>
    public AccountSession? Find(string loginId) =>
        accounts.Find(Normalize(loginId)) is { } account ? new AccountSession(account.LoginId, account.DeviceId, account.GuestUid) : null;

    /// <summary>The player of an account, or null when the game never created it.</summary>
    public PlayerRecord? FindPlayer(AccountSession account) =>
        players.FindByDevice(account.DeviceId) ?? players.FindByLinkedDevice(account.DeviceId);

    /// <summary>Checks the current password, then stores the new one.</summary>
    public AccountFailure ChangePassword(string loginId, string? oldPassword, string? newPassword)
    {
        if (!IsValidPassword(newPassword))
        {
            return AccountFailure.InvalidPassword;
        }

        if (Login(loginId, oldPassword).Session is null)
        {
            return throttle.IsLocked(Normalize(loginId)) ? AccountFailure.TooManyAttempts : AccountFailure.WrongCredentials;
        }

        accounts.SetPasswordHash(Normalize(loginId), HashPassword(newPassword!));
        return AccountFailure.None;
    }

    public AccountResult Login(string? rawId, string? password)
    {
        string loginId = Normalize(rawId);
        if (throttle.IsLocked(loginId))
        {
            return Failed(AccountFailure.TooManyAttempts);
        }

        LoginAccountRecord? account = accounts.Find(loginId);
        bool matches = VerifyPassword(password ?? "", account?.PasswordHash ?? DecoyHash);
        if (account is null || !matches)
        {
            throttle.RecordFailure(loginId);
            return Failed(AccountFailure.WrongCredentials);
        }

        throttle.Clear(loginId);
        return new AccountResult(new AccountSession(account.LoginId, account.DeviceId, account.GuestUid), AccountFailure.None);
    }

    private static AccountResult Failed(AccountFailure failure) => new(null, failure);

    /// <summary>The register username rules; the same check <c>/account/nickname</c> applies.</summary>
    public static bool IsValidNickname(string nickname) =>
        nickname.Length > 0 && Encoding.UTF8.GetByteCount(nickname) <= 24 && !nickname.Any(char.IsControl);

    private static bool IsValidPassword(string? password) =>
        password is not null && password.Length is >= MinPasswordLength and <= MaxPasswordLength;

    private static string Normalize(string? rawId) => (rawId ?? "").Trim().ToLowerInvariant();

    internal static string NewGuestUid() =>
        "906" + RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000).ToString(CultureInfo.InvariantCulture);

    private static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Create(CultureInfo.InvariantCulture, $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>
    /// pbkdf2-sha256$iterations$salt$digest with standard base64, or pbkdf2_sha256 with URL-safe unpadded base64.
    /// A malformed hash never matches.
    /// </summary>
    internal static bool VerifyPassword(string password, string stored)
    {
        string[] parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] is not ("pbkdf2-sha256" or "pbkdf2_sha256")
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations) || iterations is < 1 or > 5_000_000
            || DecodeBase64(parts[2]) is not { } salt || DecodeBase64(parts[3]) is not { Length: > 0 } expected)
        {
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[]? DecodeBase64(string text)
    {
        string standard = text.Replace('-', '+').Replace('_', '/');
        standard = standard.PadRight(standard.Length + (4 - standard.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(standard);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// A browser or Android guest's device id (java_shim.js / KnsHub.java did()). The game copies it into a small fixed buffer:
    /// a 33-character id spilled into the next field and reached protocol 51 as 42 characters. Guest ids have 20–21 chars, new account ids 20.
    /// </summary>
    [GeneratedRegex("^(web|and)-[a-z0-9-]{1,24}$")]
    internal static partial Regex DevicePattern();

    [GeneratedRegex("^[0-9]{1,20}$")]
    internal static partial Regex GuestPattern();

    [GeneratedRegex("^[a-z0-9_-]{3,24}$")]
    internal static partial Regex IdPattern();
}
