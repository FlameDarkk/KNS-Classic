using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Inventory;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Rules;

namespace KnS.Game.Friends;

/// <summary>One entry of friend_info, parsed by ParsingDataToFriendInfo@0x14c0d9.</summary>
public record FriendInfo
{
    [JsonPropertyName("user_no")] public long UserNumber { get; init; }

    /// <summary>The uid the client keeps for this friend: the friend's user number (see <see cref="FriendService.HubUidOf"/>).</summary>
    [JsonPropertyName("user_hub_uid")] public string HubUid { get; init; } = "";

    [JsonPropertyName("user_hub_id")] public string HubId { get; init; } = "";
    [JsonPropertyName("user_nickname")] public string Nickname { get; init; } = "";
    [JsonPropertyName("user_level")] public long Level { get; init; }
    [JsonPropertyName("user_last_login_time")] public long LastLoginTime { get; init; }
    [JsonPropertyName("remain_time")] public long RemainTime { get; init; }
    [JsonPropertyName("help_remain_time")] public long HelpRemainTime { get; init; }
    [JsonPropertyName("user_hero_pcid")] public long HeroPcid { get; init; }
    [JsonPropertyName("user_hero_item_id")] public long HeroItemId { get; init; }
    [JsonPropertyName("hero_attack")] public long HeroAttack { get; init; }
    [JsonPropertyName("hero_defence")] public long HeroDefence { get; init; }
    [JsonPropertyName("hero_hp")] public long HeroHp { get; init; }
    [JsonPropertyName("send_friendship_point_remain_time")] public long SendRemainTime { get; init; }
}

/// <summary>The friend graph over real players. The Hub uid a friend has on the client side is simply the user number.</summary>
public sealed class FriendService(
    FriendRepository friends, PlayerRepository players, GoodsRepository goods, MailRepository mail, PlayerProfileService profiles, InventoryService inventory, TimeProvider clock)
{
    public const int MaxFriends = 30;

    public static string HubUidOf(long userNumber) => userNumber.ToString();

    public IReadOnlyList<Player> GetFriends(long userNumber) => Resolve(friends.GetFriends(userNumber));

    public IReadOnlyList<Player> GetIncoming(long userNumber) => Resolve(friends.GetIncoming(userNumber));

    /// <summary>Players this one could ask: not a friend, not asked yet, not themself.</summary>
    public IReadOnlyList<Player> GetSuggestions(long userNumber, int limit)
    {
        HashSet<long> known = friends.GetFriends(userNumber).Concat(friends.GetOutgoing(userNumber)).ToHashSet();
        return players.ListOthers(userNumber, limit + known.Count)
            .Where(p => !known.Contains(p.UserNumber)).Take(limit).Select(Player.From).ToList();
    }

    public IReadOnlyList<Player> Lookup(IEnumerable<long> numbers) => Resolve(numbers.Where(n => n > 0));

    public IReadOnlyList<Player> GetEveryoneElse(long userNumber, int limit) =>
        players.ListOthers(userNumber, limit).Select(Player.From).ToList();

    public IReadOnlyList<Player> Search(long userNumber, string name)
    {
        string wanted = name.Trim();
        var found = new List<Player>();
        if (wanted.Length == 0)
        {
            return found;
        }

        found.AddRange(players.FindByNickname(wanted).Select(Player.From));
        string prefix = PlayerRules.DefaultNickname(0)[..^1];
        if (wanted.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(wanted[prefix.Length..], out long number)
            && players.FindByUserNumber(number) is { } byNumber
            && found.All(p => p.UserNumber != number))
        {
            found.Add(Player.From(byNumber));
        }

        return found.Where(p => p.UserNumber != userNumber).ToList();
    }

    /// <summary>Asks each player to be a friend. A request they already sent you becomes a friendship at once. Returns how many were sent.</summary>
    public IReadOnlyList<long> SendRequests(long userNumber, IEnumerable<long> targets)
    {
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var sent = new List<long>();
        foreach (long target in targets.Distinct().Where(t => t > 0 && t != userNumber && players.FindByUserNumber(t) is not null))
        {
            if (friends.GetStatus(userNumber, target) is not null)
            {
                continue;
            }

            if (friends.CountFriends(userNumber) >= MaxFriends || friends.CountFriends(target) >= MaxFriends)
            {
                continue;
            }

            if (friends.GetStatus(target, userNumber) == FriendRepository.Requested)
            {
                friends.Accept(target, userNumber, now);
            }
            else
            {
                friends.Request(userNumber, target, now);
            }

            sent.Add(target);
        }

        return sent;
    }

    /// <summary>
    /// Sends friendship points to friends whose cool time is over; each one pays the sender <see cref="FriendRules.SendReward"/> points.
    /// Returns how many went out and how many points were earned.
    /// </summary>
    public (int Sent, long Points) SendPoints(long userNumber, IEnumerable<long> targets)
    {
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        int sent = 0;
        foreach (long target in targets.Distinct())
        {
            if (friends.GetStatus(userNumber, target) != FriendRepository.Accepted
                || friends.GetLastSent(userNumber, target) + FriendRules.SendCooldownSeconds > now)
            {
                continue;
            }

            friends.SetLastSent(userNumber, target, now);
            MailFriendPoints(userNumber, target, FriendRules.ReceiveReward, "sent you Friendship Points.", now);
            sent++;
        }

        long points = sent * FriendRules.SendReward;
        if (points > 0)
        {
            goods.Add(userNumber, CurrencyItems.FriendshipPoints, points);
        }

        return (sent, points);
    }

    /// <summary>Puts friendship points into the friend's FP tab (Message Box); they are claimed there.</summary>
    private void MailFriendPoints(long fromUser, long toUser, long points, string text, long now) =>
        mail.Add(toUser, CurrencyItems.FriendshipPoints, points, message: text, type: FriendRules.FriendPointMailType, regDate: now,
            title: players.FindByUserNumber(fromUser)?.Nickname ?? "Friend");

    /// <summary>
    /// A friend's hero helped the player in a dungeon: the helper gets <see cref="FriendRules.HelperReward"/> points in the FP tab,
    /// at most once per <see cref="FriendRules.HelpCooldownSeconds"/> for each pair. Returns whether points were sent.
    /// </summary>
    public bool RewardHelper(long userNumber, long helperNumber)
    {
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (helperNumber <= 0 || helperNumber == userNumber
            || friends.GetStatus(userNumber, helperNumber) != FriendRepository.Accepted
            || friends.GetLastHelp(userNumber, helperNumber) + FriendRules.HelpCooldownSeconds > now)
        {
            return false;
        }

        friends.SetLastHelp(userNumber, helperNumber, now);
        MailFriendPoints(userNumber, helperNumber, FriendRules.HelperReward, "helped you in a dungeon.", now);
        return true;
    }

    public int SentToday(long userNumber) =>
        friends.CountSentSince(userNumber, clock.GetUtcNow().ToUnixTimeSeconds() - FriendRules.SendCooldownSeconds);

    /// <summary>Accepts the requests these players sent. Returns the players who are now friends.</summary>
    public IReadOnlyList<Player> AcceptRequests(long userNumber, IEnumerable<long> requesters)
    {
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var accepted = new List<long>();
        foreach (long requester in requesters.Distinct())
        {
            if (friends.GetStatus(requester, userNumber) == FriendRepository.Requested
                && friends.CountFriends(userNumber) < MaxFriends && friends.CountFriends(requester) < MaxFriends)
            {
                friends.Accept(requester, userNumber, now);
                accepted.Add(requester);
            }
        }

        return Resolve(accepted);
    }

    /// <summary>Refuses the requests these players sent; friendships are left alone.</summary>
    public void RemoveRequests(long userNumber, IEnumerable<long> requesters)
    {
        foreach (long requester in requesters.Distinct())
        {
            if (friends.GetStatus(requester, userNumber) == FriendRepository.Requested)
            {
                friends.Remove(userNumber, requester);
            }
        }
    }

    /// <summary>Drops a friendship (or withdraws a request): the links between the two go away.</summary>
    public void RemoveLinks(long userNumber, IEnumerable<long> others)
    {
        foreach (long other in others.Distinct())
        {
            friends.Remove(userNumber, other);
        }
    }

    public IReadOnlyList<long> GetOutgoingNumbers(long userNumber) => friends.GetOutgoing(userNumber);

    public int CountOutgoing(long userNumber) => friends.GetOutgoing(userNumber).Count;

    public FriendInfo ToInfo(Player player, long viewer)
    {
        UserInfo info = profiles.BuildUserInfo(player);
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        return new FriendInfo
        {
            UserNumber = player.UserNumber,
            HubUid = HubUidOf(player.UserNumber),
            HubId = player.Nickname,
            Nickname = player.Nickname,
            Level = info.Level,
            LastLoginTime = LastPlayed(player.UserNumber, viewer, now),
            HeroPcid = info.ActiveHeroPcid,
            HeroItemId = inventory.GetActiveArmorItemId(player.UserNumber, info.ActiveHeroPcid),
            HeroAttack = info.HeroAttack,
            HeroDefence = info.HeroDefence,
            HeroHp = info.HeroHp,
            SendRemainTime = Math.Max(0, friends.GetLastSent(viewer, player.UserNumber) + FriendRules.SendCooldownSeconds - now),
        };
    }

    /// <summary>
    /// DrawPlayerDetailInfo shows (client clock base - user_last_login_time) / 60 minutes, where the base is the server_time from the viewer's
    /// last protocol 60 and does not advance. Clamping to that base keeps a player active since then at "0 min ago" instead of negative.
    /// </summary>
    private long LastPlayed(long userNumber, long viewer, long now)
    {
        long seen = players.GetActivity(userNumber).Seen;
        long sync = players.GetActivity(viewer).Sync;
        long limit = sync > 0 ? Math.Min(sync, now) : now;
        return seen > 0 ? Math.Min(seen, limit) : limit;
    }

    private List<Player> Resolve(IEnumerable<long> numbers) =>
        numbers.Select(players.FindByUserNumber).OfType<PlayerRecord>().Select(Player.From).ToList();
}
