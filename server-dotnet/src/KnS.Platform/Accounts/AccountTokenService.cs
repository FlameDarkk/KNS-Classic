using System.Security.Cryptography;
using System.Text;
using KnS.Data.Repositories;

namespace KnS.Platform.Accounts;

/// <summary>
/// Sign-in tokens of id + password accounts: random 32 bytes, stored only as a SHA-256 hash. A token lasts 30 days and every use
/// renews it; logout removes it and a password change removes every other one.
/// </summary>
public sealed class AccountTokenService(AccountSessionRepository sessions, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private const int MaxTokenLength = 128;

    public string Issue(string loginId)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        double now = Now;
        sessions.RemoveExpired(now);
        sessions.Add(Hash(token), loginId, now, now + Lifetime.TotalSeconds);
        return token;
    }

    /// <summary>The login id of a live token, renewing it; null when it is unknown or expired.</summary>
    public string? Verify(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return null;
        }

        string hash = Hash(token);
        double now = Now;
        if (sessions.FindLogin(hash, now) is not { } loginId)
        {
            return null;
        }

        sessions.Extend(hash, now + Lifetime.TotalSeconds);
        return loginId;
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrEmpty(token) && token.Length <= MaxTokenLength)
        {
            sessions.Remove(Hash(token));
        }
    }

    /// <summary>Signs the id out of every other browser.</summary>
    public void RevokeOthers(string loginId, string keptToken) => sessions.RemoveOthers(loginId, Hash(keptToken));

    private double Now => clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
