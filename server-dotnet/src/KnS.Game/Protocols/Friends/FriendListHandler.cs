using System.Text.Json.Serialization;
using KnS.Game.Friends;
using KnS.Game.Gateway;
using KnS.Game.Players;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Friends;

/// <summary>
/// Protocol 62 (parser case @0x15be26), request <c>{user_no, type 5, max_friend_count, page, count, friend_list: [{user_no, hub_uid}]}</c>, sent
/// after the Hub's friends/game list arrives. The reply gives the game data of every listed player (hub_uid is their user number); the client
/// shows those who are not friends yet in Add Friends.
/// </summary>
public sealed class FriendListHandler(PlayerDirectory directory, FriendService friends, ILogger<FriendListHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long type = request.Parameters.GetInt64("type");
        long page = request.Parameters.GetInt64("page", 1);
        // The Hub list is everyone else, and the client builds the Add Friends tab from the game data returned for each of them.
        List<FriendInfo> list = friends.Lookup(request.Parameters.GetList("friend_list")
            .Select(entry => long.TryParse(entry?["hub_uid"]?.ToString(), out long number) ? number : 0)).Select(p => friends.ToInfo(p, userNumber)).ToList();
        logger.LogInformation("Friend list: params {Params} -> {Count} friends", request.Parameters.ToJsonString(), list.Count);

        // The client keeps asking for the next page until count * page >= max_friend_count, so both echo the request: max_friend_count is
        // the size of the Hub list it sent and count is its page size.
        return ProtocolResult.Success(new FriendListResponse
        {
            Type = type,
            MaxFriendCount = request.Parameters.GetInt64("max_friend_count"),
            Count = request.Parameters.GetInt64("count", 50),
            Page = page,
            Friends = list,
        });
    }
}

public sealed record FriendListResponse
{
    [JsonPropertyName("type")] public long Type { get; init; }
    [JsonPropertyName("max_friend_count")] public long MaxFriendCount { get; init; }
    [JsonPropertyName("count")] public long Count { get; init; }
    [JsonPropertyName("page")] public long Page { get; init; }
    [JsonPropertyName("friend_info")] public IReadOnlyList<FriendInfo> Friends { get; init; } = [];
}
