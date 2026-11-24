using System.Text.Json.Serialization;
using KnS.Game.Fellows;
using KnS.Game.Friends;
using KnS.Game.Inventory;
using KnS.Data.Repositories;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Rules;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Friends;

/// <summary>Protocol 801 (parser case @0x15f17c): ask players to be friends, <c>{friend_list: [user_no]}</c>.</summary>
public sealed class FriendRequestHandler(PlayerDirectory directory, FriendService friends) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        IEnumerable<long> targets = request.Parameters.GetList("friend_list")
            .Select(entry => long.TryParse(entry?.ToString(), out long number) ? number : 0);
        IReadOnlyList<long> sent = friends.SendRequests(userNumber, targets);
        return ProtocolResult.Success(new FriendRequestResponse(friends.CountOutgoing(userNumber), FriendRules.RequestCooldownSeconds, sent));
    }
}

public sealed record FriendRequestResponse(
    [property: JsonPropertyName("friend_request_count")] long RequestCount,
    [property: JsonPropertyName("remain_time")] long RemainTime,
    [property: JsonPropertyName("success_list")] IReadOnlyList<long> SuccessList);

/// <summary>Protocol 113 (parser case @0x15c2a4): the requests other players sent this player (the Accept tab), no parameters.</summary>
public sealed class FriendIncomingHandler(PlayerDirectory directory, FriendService friends) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        return ProtocolResult.Success(new FriendIncomingResponse(
            friends.GetIncoming(userNumber).Select(p => friends.ToInfo(p, userNumber)).ToList()));
    }
}

public sealed record FriendIncomingResponse(
    [property: JsonPropertyName("friend_request_user_info")] IReadOnlyList<FriendInfo> Requests);

/// <summary>Protocol 804 (parser case @0x15b496): the players this player asked and who have not answered, <c>my_request_info</c>.</summary>
public sealed class FriendOutgoingHandler(PlayerDirectory directory, FriendService friends) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        return ProtocolResult.Success(new FriendOutgoingResponse(
            friends.GetOutgoingNumbers(userNumber).Select(number => new UserNoEntry(number, FriendRules.RequestCooldownSeconds)).ToList()));
    }
}

public sealed record FriendOutgoingResponse(
    [property: JsonPropertyName("my_request_info")] IReadOnlyList<UserNoEntry> Requests);

public sealed record UserNoEntry(
    [property: JsonPropertyName("user_no")] long UserNumber,
    [property: JsonPropertyName("remain_time")] long RemainTime);

/// <summary>Protocol 803 (parser case @0x15d76e): this player's friends (My Friends), <c>{page, count}</c>, with the daily request and delete counters.</summary>
public sealed class FriendRequestListHandler(PlayerDirectory directory, FriendService friends, ILogger<FriendRequestListHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        List<FriendInfo> list = friends.GetFriends(userNumber).Select(p => friends.ToInfo(p, userNumber)).ToList();
        logger.LogInformation("Friend list (803): {Count} friends", list.Count);
        return ProtocolResult.Success(new FriendRequestListResponse
        {
            Count = list.Count,
            Page = request.Parameters.GetInt64("page", 1),
            RequestCount = friends.CountOutgoing(userNumber),
            Friends = list,
        });
    }
}

public sealed record FriendRequestListResponse
{
    [JsonPropertyName("count")] public long Count { get; init; }
    [JsonPropertyName("page")] public long Page { get; init; }
    [JsonPropertyName("friend_request_count")] public long RequestCount { get; init; }
    [JsonPropertyName("friend_del_count")] public long DeleteCount { get; init; }
    [JsonPropertyName("reset_time")] public long ResetTime { get; init; }
    [JsonPropertyName("friend_info")] public IReadOnlyList<FriendInfo> Friends { get; init; } = [];
}

/// <summary>
/// Protocol 116 (parser case @0x160788): send friendship points, <c>{friend_list: [user_no], achievement_list}</c>.
/// The reply carries the daily counters; the points themselves arrive with the next item list.
/// Native protocol 116 also parses friend_info and copies those records into the saved friend list.
/// </summary>
public sealed class FriendSendHandler(PlayerDirectory directory, FriendService friends, PlayerProfileService profiles,
    DailyQuestRepository quests, TimeProvider clock, AchievementRepository achievements) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        IEnumerable<long> targets = request.Parameters.GetList("friend_list")
            .Select(entry => long.TryParse(entry?.ToString(), out long number) ? number : 0);
        (int sent, _) = friends.SendPoints(userNumber, targets);
        if (sent > 0)
        {
            achievements.AddProgress(userNumber, 40067, sent);
            quests.Add(userNumber, clock.GetUtcNow().ToUnixTimeSeconds() / 86400, 5300005, sent);
        }
        return ProtocolResult.Success(new FriendSendResponse
        {
            DailyReward = new Protocols.Profile.DailyRewardInfo { SendCount = friends.SentToday(userNumber) },
            DailyQuest = Quests.DailyQuestInfo.Current(quests, clock, userNumber, 5300005),
            Goods = profiles.GetGoods(userNumber),
            Friends = friends.GetFriends(userNumber).Select(p => friends.ToInfo(p, userNumber)).ToList(),
        });
    }
}

public sealed record FriendSendResponse
{
    [JsonPropertyName("friend_info")] public IReadOnlyList<FriendInfo> Friends { get; init; } = [];
    [JsonPropertyName("daily_quest_info")] public required Quests.DailyQuestInfo DailyQuest { get; init; }
    [JsonPropertyName("daily_reward_info")] public Protocols.Profile.DailyRewardInfo DailyReward { get; init; } = new();
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<Models.GoodsEntry> Goods { get; init; } = [];
}

/// <summary>Protocol 111 (parser case @0x15db68): accept friend requests, <c>{friend_list: [user_no]}</c>; the reply lists the new friends.</summary>
public sealed class FriendAcceptHandler(PlayerDirectory directory, FriendService friends) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        IReadOnlyList<Player> accepted = friends.AcceptRequests(userNumber, FriendNumbers.From(request));
        return ProtocolResult.Success(new FriendAcceptResponse(accepted.Select(p => friends.ToInfo(p, userNumber)).ToList()));
    }
}

public sealed record FriendAcceptResponse([property: JsonPropertyName("friend_info")] IReadOnlyList<FriendInfo> Friends);

/// <summary>Protocol 110 (no reply fields are read): withdraw requests, <c>{friend_list: [user_no]}</c>.</summary>
public sealed class FriendDenyHandler(PlayerDirectory directory, FriendService friends) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        friends.RemoveLinks(directory.ResolveCurrent(request).UserNumber, FriendNumbers.From(request));
        return ProtocolResult.Success(new EmptyReply());
    }
}

/// <summary>Protocol 112 (parser case @0x15ca62): delete a friend, <c>{friend_user_no}</c>.</summary>
public sealed class FriendDeleteHandler(PlayerDirectory directory, FriendService friends, ILogger<FriendDeleteHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        logger.LogInformation("Friend delete: {Params}", request.Parameters.ToJsonString());
        long friendNumber = request.Parameters.GetInt64("friend_user_no");
        friends.RemoveLinks(directory.ResolveCurrent(request).UserNumber, [friendNumber]);
        return ProtocolResult.Success(new FriendDeleteResponse(friendNumber, 0));
    }
}

public sealed record FriendDeleteResponse(
    [property: JsonPropertyName("deleted_user_no")] long DeletedUserNumber,
    [property: JsonPropertyName("friend_del_count")] long DeleteCount);

public sealed record EmptyReply;

internal static class FriendNumbers
{
    /// <summary>The user numbers of a request's friend_list.</summary>
    public static IEnumerable<long> From(GatewayRequest request) =>
        request.Parameters.GetList("friend_list").Select(entry => long.TryParse(entry?.ToString(), out long number) ? number : 0);
}

/// <summary>
/// Protocol 64 (parser case @0x160408): a friend's profile card, <c>{friend_user_no, type}</c>: level, the hero with its gear, allies
/// and the ally gear they wear, plus the current Arena and Raid ranks.
/// </summary>
public sealed class FriendDetailHandler(
    FriendService friends,
    PlayerProfileService profiles,
    InventoryService inventory,
    FellowService fellows,
    FellowItemService fellowItems,
    Arena.ArenaService arena,
    Raid.RaidService raid) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long friendNumber = request.Parameters.GetInt64("friend_user_no");
        if (friends.Lookup([friendNumber]) is not [{ } friend, ..])
        {
            return ProtocolResult.Refused("no such player");
        }

        UserInfo info = profiles.BuildUserInfo(friend);
        IReadOnlyList<OwnedHeroRecord> owned = inventory.GetHeroes(friendNumber);
        OwnedHeroRecord? hero = owned.FirstOrDefault(h => h.HeroPcid == info.ActiveHeroPcid) ?? (owned.Count > 0 ? owned[0] : null);
        IReadOnlyList<FellowInfo> party = fellows.GetProfileParty(friendNumber);
        HashSet<long> partyNumbers = party.Where(fellow => fellow.FellowNumber > 0).Select(fellow => fellow.FellowNumber).ToHashSet();
        return ProtocolResult.Success(new FriendDetailResponse
        {
            Type = request.Parameters.GetInt64("type"),
            Level = info.Level,
            UserNumber = friendNumber,
            HubUid = FriendService.HubUidOf(friendNumber),
            Hero = hero is null ? null : inventory.BuildHeroInfo(friendNumber, hero.HeroNumber, hero.HeroPcid),
            Allies = party,
            AllyItems = fellowItems.GetOwned(friendNumber).Where(item => partyNumbers.Contains(item.FellowNumber)).ToList(),
            // DrawPlayerDetailInfo prints the rank when > 0, else "No Records".
            PvpRank = arena.ProfileRank(friendNumber),
            RaidRank = raid.GetUserInfo(friendNumber) is { Rank: > 0 } raidInfo ? raidInfo.Rank : 0,
        });
    }
}

public sealed record FriendDetailResponse
{
    [JsonPropertyName("type")] public long Type { get; init; }
    [JsonPropertyName("user_level")] public long Level { get; init; }
    [JsonPropertyName("user_no")] public long UserNumber { get; init; }
    [JsonPropertyName("user_hub_uid")] public string HubUid { get; init; } = "";
    [JsonPropertyName("hero_info")] public HeroInfo? Hero { get; init; }
    [JsonPropertyName("fellow_info")] public IReadOnlyList<FellowInfo> Allies { get; init; } = [];
    [JsonPropertyName("pvp_rank")] public long PvpRank { get; init; }
    [JsonPropertyName("raid_rank")] public long RaidRank { get; init; }
    [JsonPropertyName("fellow_item_infos")] public IReadOnlyList<FellowItemInfo> AllyItems { get; init; } = [];
}

/// <summary>Protocol 802 (parser case @0x15f120): Deny on the Accept tab, <c>{reject_list: [user_no]}</c>; the reply lists who was refused.</summary>
public sealed class FriendRejectHandler(PlayerDirectory directory, FriendService friends) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        List<long> rejected = request.Parameters.GetList("reject_list")
            .Select(entry => long.TryParse(entry?.ToString(), out long number) ? number : 0).Where(n => n > 0).ToList();
        friends.RemoveRequests(directory.ResolveCurrent(request).UserNumber, rejected);
        return ProtocolResult.Success(new FriendRejectResponse(rejected));
    }
}

public sealed record FriendRejectResponse([property: JsonPropertyName("success_list")] IReadOnlyList<long> SuccessList);
