using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Inventory;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Protocols.Profile;
using KnS.Game.Rewards;
using KnS.Game.Rules;
using KnS.Protocol.Json;

namespace KnS.Game.Protocols.Mail;

/// <summary>Protocol 63: native mailbox pages, filtered separately from message categories.</summary>
public sealed class MailListHandler(PlayerDirectory directory, MailRepository mail) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        long type = request.Parameters.GetInt64("type", 2);
        long page = request.Parameters.GetInt64("page", 1);
        long count = request.Parameters.GetInt64("count", 30);
        if (type is not (1 or 2) || page < 1 || page > int.MaxValue || count < 1 || count > 100)
            return ProtocolResult.Refused("invalid mailbox page");
        return ProtocolResult.Success(new MailListResponse
        {
            Type = type, Total = mail.Count(user, type),
            Messages = mail.List(user, type, (page - 1) * count, count).Select(row => new MailInfo(row)).ToList(),
        });
    }
}

public sealed record MailListResponse
{
    [JsonPropertyName("mail_info")] public IReadOnlyList<MailInfo> Messages { get; init; } = [];
    [JsonPropertyName("total_mail_count")] public long Total { get; init; }
    [JsonPropertyName("type")] public long Type { get; init; }
}

public sealed record MailInfo(MailRecord Record)
{
    [JsonIgnore] public MailRecord Record { get; init; } = Record;
    [JsonPropertyName("mail_no")] public long Number => Record.MailNumber;
    [JsonPropertyName("recv_user_no")] public long Receiver => Record.UserNumber;
    [JsonPropertyName("send_user_no")] public long Sender { get; init; }
    [JsonPropertyName("send_user_hub_uid")] public string SenderUid { get; init; } = "";
    [JsonPropertyName("send_user_hub_id")] public string SenderName => Record.TextCode == 0 && Record.Title.Length > 0 ? Record.Title : "System";
    [JsonPropertyName("message")] public string Message => Record.TextCode == 0 && Record.Title.Length > 0
        ? (Record.Message.Length > 0 ? $"{Record.Title}\n{Record.Message}" : Record.Title)
        : Record.Message;
    [JsonPropertyName("text_code")] public long TextCode => MailTexts.ForClient(Record.TextCode);
    [JsonPropertyName("type")] public long Type => Record.Type;
    [JsonPropertyName("item_id1")] public long Item1 => Record.ItemId1;
    [JsonPropertyName("value1")] public long Value1 => Record.Value1;
    [JsonPropertyName("item_id2")] public long Item2 => Record.ItemId2;
    [JsonPropertyName("value2")] public long Value2 => Record.Value2;
    [JsonPropertyName("reg_date")] public long Date => Record.RegDate;
}

/// <summary>Protocol 115 returns arrays for items and allies, including multi-ally rewards. Any master ally, equipment or
/// material (e.g. XP potions) can be a reward; counted etc items (gold, gems, stamina, tickets) are added to the wallet.</summary>
public sealed class MailClaimHandler(
    PlayerDirectory directory, MailRepository mail, MasterDataRepository master,
    PlayerProfileService profiles, PlayerRepository players, GoodsRepository goods,
    FellowService fellows, ItemRepository items, InventoryService inventory, FellowDrawAuditRepository audit, TimeProvider clock) : IProtocolHandler
{
    private static readonly Dictionary<long, long> GradeAllyTickets = new()
    {
        [1900018] = 4,
        [1900019] = 5,
        [1900020] = 6,
    };

    public ProtocolResult Handle(GatewayRequest request)
    {
        Player player = directory.ResolveCurrent(request);
        long user = player.UserNumber;
        IReadOnlyList<System.Text.Json.Nodes.JsonNode?> raw = request.Parameters.GetList("mail_list");
        long[] numbers = raw.Select(value => LenientJson.TryToInt64(value) ?? 0).ToArray();
        if (numbers.Length is < 1 or > 100 || numbers.Any(number => number <= 0) || numbers.Distinct().Count() != numbers.Length)
            return ProtocolResult.Refused("invalid message selection");

        mail.LockUser(user);
        var selected = new List<MailRecord>();
        var rewards = new List<(long Item, long Count)>();
        HashSet<long> counted = master.GetEtcItemIds();
        HashSet<long> materials = master.GetMaterialItemIds();
        bool IsAlly(long item) => master.FellowExists(item);
        bool IsItem(long item) => master.ItemExists(item) || materials.Contains(item);
        foreach (long number in numbers)
        {
            MailRecord? row = mail.Find(user, number);
            if (row is null) return ProtocolResult.Refused("message is unavailable");
            selected.Add(row);
            foreach ((long item, long count) in new[] { (row.ItemId1, row.Value1), (row.ItemId2, row.Value2) })
            {
                if (item == 0 && count == 0) continue;
                bool single = IsAlly(item) || IsItem(item);
                if (count <= 0 || count > int.MaxValue || (!single && !counted.Contains(item)) || (single && count > 1000))
                    return ProtocolResult.Refused("unsupported message reward");
                rewards.Add((item, count));
            }
        }

        HashSet<long> allyNumbers = [];
        HashSet<long> itemNumbers = [];
        // The native mailbox opens the ally draw result immediately for star-grade ally tickets (1900018/1900019/1900020).
        // Resolve every draw before deleting any mail, so an empty pool cannot consume it.
        var ticketDraws = new List<(long TicketItem, long FellowId)>();
        foreach ((long item, long count) in rewards)
        {
            if (!GradeAllyTickets.TryGetValue(item, out long grade)) continue;
            if (count > 1000 || ticketDraws.Count + count > 1000) return ProtocolResult.Refused("too many ally tickets");
            for (long i = 0; i < count; i++)
            {
                long? drawn = fellows.PickDrawableOfGrade(grade);
                if (drawn is null) return ProtocolResult.Refused($"no {grade}-star allies available");
                ticketDraws.Add((item, drawn.Value));
            }
        }
        foreach (MailRecord row in selected) mail.Delete(user, row.MailNumber);
        string username = audit.Username(user);
        string batchId = Guid.NewGuid().ToString("N");
        long timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        foreach ((long ticketItem, long fellowId) in ticketDraws)
        {
            long fellowNumber = fellows.Grant(user, fellowId);
            audit.Record(user, username, player.Nickname, batchId, timestamp, "mail_ticket", 0, ticketItem, 1, fellowNumber, fellowId);
            allyNumbers.Add(fellowNumber);
        }
        foreach ((long item, long count) in rewards)
        {
            if (GradeAllyTickets.ContainsKey(item)) continue;
            if (item == CurrencyItems.Gold) profiles.AddCurrency(user, gold: count);
            else if (item == CurrencyItems.Gems) profiles.AddCurrency(user, gems: count);
            else if (item == CurrencyItems.Stamina)
            {
                (long stamina, long lastGain) = profiles.GetStamina(user);
                players.SetStamina(user, checked(stamina + count), lastGain);
            }
            else if (IsAlly(item))
            {
                for (long i = 0; i < count; i++) allyNumbers.Add(fellows.Grant(user, item));
            }
            else if (IsItem(item))
            {
                for (long i = 0; i < count; i++)
                    itemNumbers.Add(items.Add(user, item, true, inventory.GetNewItemStats(item)));
            }
            else goods.Add(user, item, count);
        }
        return ProtocolResult.Success(new MailClaimResponse
        {
            Numbers = numbers, User = profiles.BuildUserInfo(player), Goods = profiles.GetGoods(user),
            Fellows = fellows.GetFellowInfos(user, allyNumbers),
            Items = inventory.GetOwnedItems(user, inventory.GetWornItems(user)).Where(item => itemNumbers.Contains(item.ItemNumber)).ToList(),
        });
    }
}

public sealed record MailClaimResponse
{
    [JsonPropertyName("mail_list")] public IReadOnlyList<long> Numbers { get; init; } = [];
    [JsonPropertyName("user_info")] public required UserInfo User { get; init; }
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("fellow_info")] public IReadOnlyList<FellowInfo> Fellows { get; init; } = [];
    [JsonPropertyName("item_info")] public IReadOnlyList<ItemInfo> Items { get; init; } = [];
    [JsonPropertyName("daily_reward_info")] public DailyRewardInfo DailyReward { get; init; } = new();
    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
}
