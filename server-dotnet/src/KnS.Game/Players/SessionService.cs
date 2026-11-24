using System.Security.Cryptography;
using System.Text;
using KnS.Data.Repositories;

namespace KnS.Game.Players;

/// <summary>
/// The session key protocol 51 issues. The client echoes it as the SESSIONKEY header next to USERNO on every later request, which is
/// what proves a request's user number. A player keeps a few sessions, so a phone and a browser can play the same account.
/// </summary>
public sealed class SessionService(SessionRepository sessions, TimeProvider clock)
{
    /// <summary>80 bits as 20 hex characters. Keep it short: the game copies ids like this into small fixed buffers.</summary>
    private const int KeyBytes = 10;

    private const int SessionsPerPlayer = 5;

    public string Issue(long userNumber)
    {
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(KeyBytes)).ToLowerInvariant();
        sessions.Add(Hash(key), userNumber, clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0);
        sessions.KeepNewest(userNumber, SessionsPerPlayer);
        return key;
    }

    public bool IsValid(long userNumber, string? key) =>
        userNumber != 0 && !string.IsNullOrEmpty(key) && sessions.Exists(Hash(key), userNumber);

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
