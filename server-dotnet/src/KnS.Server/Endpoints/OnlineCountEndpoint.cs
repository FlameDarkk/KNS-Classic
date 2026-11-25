using KnS.Data;
using KnS.Data.Repositories;
using KnS.Game.Rules;

namespace KnS.Server.Endpoints;

/// <summary>
/// GET or POST /api/online: {"online": n, "players": [nickname, ...]}, the players with an authorized request in the last
/// <see cref="WindowSeconds"/> (gameuser.last_seen, refreshed by game requests and authenticated browser heartbeats,
/// at most once a minute). Anonymous polls only read presence. The browser page shows
/// the count under the side buttons and the names in a popup over it. Public: it reveals only the in-game nicknames that
/// chat and Friends already show. Served under the API base like every other route.
/// </summary>
public static class OnlineCountEndpoint
{
    public const string Path = "/api/online";
    public const long WindowSeconds = 300;

    public static bool Matches(string target) =>
        string.Equals(target.Split('?', 2)[0].TrimEnd('/'), Path, StringComparison.OrdinalIgnoreCase);

    public static OnlineCountReply Read(DataSession session, long now)
    {
        IReadOnlyList<PlayerRecord> active = session.Query<PlayerRecord>(
            "SELECT user_no AS UserNumber, nickname AS Nickname FROM gameuser WHERE last_seen>=@since",
            new { since = now - WindowSeconds });
        List<string> players = active.Select(player => string.IsNullOrWhiteSpace(player.Nickname)
            ? PlayerRules.DefaultNickname(player.UserNumber)
            : player.Nickname.Trim()).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new OnlineCountReply(players.Count, players);
    }
}

public sealed record OnlineCountReply(
    [property: System.Text.Json.Serialization.JsonPropertyName("online")] long Online,
    [property: System.Text.Json.Serialization.JsonPropertyName("players")] IReadOnlyList<string> Players);
