using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Friends;
using KnS.Game.Players;
using KnS.Protocol;

namespace KnS.Server.Endpoints;

/// <summary>
/// The Hub's friend lists: <c>friends/game</c> (the game's friends, asked when the Friends screen opens; the client then asks gateway
/// protocol 62 with this list) and <c>friends/other</c>. The page sends its device id as <c>did</c>; a friend's Hub uid is its user number.
/// </summary>
public static class HubFriendsEndpoint
{
    public static bool Matches(string path) =>
        path.Contains("/friends/", StringComparison.Ordinal) || path.TrimEnd('/').EndsWith("/hub/users", StringComparison.Ordinal);

    public static HubFriendsReply Handle(IServiceProvider services, string path, RequestParameters parameters)
    {
        if (path.TrimEnd('/').EndsWith("/users", StringComparison.Ordinal))
        {
            return HandleUsers(services, parameters);
        }

        string deviceId = parameters.GetText("did", "");
        var players = services.GetRequiredService<PlayerRepository>();
        PlayerRecord? record = deviceId.Length == 0 ? null : players.FindByLinkedDevice(deviceId) ?? players.FindByDevice(deviceId);
        if (record is null)
        {
            return new HubFriendsReply();
        }

        var friends = services.GetRequiredService<FriendService>();
        // The Hub's "game friends" are the people it knows who play this game; here that is every other real player. The gateway
        // (protocol 62) then says which of them are friends.
        IReadOnlyList<Player> list = friends.GetEveryoneElse(record.UserNumber, 100);
        return new HubFriendsReply { Friends = list.Select(p => new HubFriend(FriendService.HubUidOf(p.UserNumber), p.Nickname)).ToList() };
    }

    /// <summary>Hub <c>users</c>: names for the uid_list the client sends after protocol 62 (hubCallbackUsersInfo reads user_list).</summary>
    private static HubFriendsReply HandleUsers(IServiceProvider services, RequestParameters parameters)
    {
        var players = services.GetRequiredService<PlayerRepository>();
        var found = new List<HubFriend>();
        foreach (var entry in parameters.GetList("uid_list"))
        {
            if (long.TryParse(entry?.ToString(), out long number) && players.FindByUserNumber(number) is { } record)
            {
                found.Add(new HubFriend(FriendService.HubUidOf(number), Player.From(record).Nickname));
            }
        }

        return new HubFriendsReply { Users = found };
    }
}

public sealed record HubFriendsReply
{
    [JsonPropertyName("user_list")] public IReadOnlyList<HubFriend> Users { get; init; } = [];
    [JsonPropertyName("error_code")] public int ErrorCode { get; init; }
    [JsonPropertyName("error_detail")] public string ErrorDetail { get; init; } = "";
    [JsonPropertyName("friend_list")] public IReadOnlyList<HubFriend> Friends { get; init; } = [];
}

/// <summary>An entry of the Hub's friend_list (ParsingDataToHubFriendInfo@0x16bbb5).</summary>
public sealed record HubFriend(
    [property: JsonPropertyName("uid")] string Uid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("id")] string Id = "",
    [property: JsonPropertyName("fbid")] string FacebookId = "",
    [property: JsonPropertyName("picture")] string Picture = "",
    [property: JsonPropertyName("country")] string Country = "");
