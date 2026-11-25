using System.Text.Json;
using System.Text.Json.Nodes;
using KnS.Data;
using KnS.Data.Repositories;
using KnS.Game.Chat;
using KnS.Game.Fellows;
using KnS.Game.Inventory;
using KnS.Game.Rules;
using KnS.Game.Protocols.Raid;
using KnS.Game.Protocols.Arena;
using KnS.Server.Configuration;

namespace KnS.Server.Endpoints;

/// <summary>Admin-key protected account inspection and bounded test edits.</summary>
public sealed class AccountEditorEndpoint(ServerOptions options)
{
    public const string PathPrefix = "/admin/accounts";
    public static bool Matches(string target) => target.Split('?', 2)[0].TrimEnd('/').Equals(PathPrefix, StringComparison.OrdinalIgnoreCase);

    public async Task ServeAsync(HttpContext context)
    {
        if (!await AdminAccess.AuthorizeAsync(context, options)) return;
        DataSession db = context.RequestServices.GetRequiredService<DataSession>();
        if (HttpMethods.IsGet(context.Request.Method))
        {
            if (context.Request.Query.ContainsKey("events"))
            {
                await AdminAccess.WriteAsync(context, 200, StudioEventEditor.Snapshot(db, context.RequestServices.GetRequiredService<TimeProvider>()));
                return;
            }
            if (context.Request.Query.ContainsKey("arena"))
            {
                db.BeginTransaction();
                var arena = context.RequestServices.GetRequiredService<ArenaService>();
                JsonObject status = ArenaStatus(arena);
                db.Commit();
                await AdminAccess.WriteAsync(context, 200, status);
                return;
            }
            if (context.Request.Query.ContainsKey("raid"))
            {
                db.BeginTransaction();
                var raid = context.RequestServices.GetRequiredService<RaidService>();
                RaidDungeonInfo current = raid.GetRaid();
                db.Commit();
                await AdminAccess.WriteAsync(context, 200, RaidStatus(current));
                return;
            }
            if (!long.TryParse(context.Request.Query["user_no"], out long user))
            {
                var accounts = db.Query<EditorAccount>("SELECT user_no AS UserNumber, nickname AS Nickname, gold AS Gold, cash AS Gems, level AS Level, exp AS Experience FROM gameuser WHERE user_no>0 ORDER BY user_no");
                await AdminAccess.WriteAsync(context, 200, new JsonObject { ["accounts"] = JsonSerializer.SerializeToNode(accounts) });
                return;
            }
            EditorAccount? account = Find(db, user);
            if (account is null) { await Error(context, 404, "account not found"); return; }
            var services = context.RequestServices;
            var snapshot = new JsonObject
            {
                ["account"] = JsonSerializer.SerializeToNode(account),
                ["goods"] = JsonSerializer.SerializeToNode(services.GetRequiredService<GoodsRepository>().GetAll(user)),
                ["items"] = JsonSerializer.SerializeToNode(services.GetRequiredService<ItemRepository>().GetOwned(user)),
                ["fellows"] = JsonSerializer.SerializeToNode(services.GetRequiredService<FellowRepository>().GetOwned(user)),
                ["fellow_items"] = JsonSerializer.SerializeToNode(services.GetRequiredService<FellowItemRepository>().GetOwned(user)),
            };
            StudioInventoryEditor.AddMetadata(snapshot, services, user);
            await AdminAccess.WriteAsync(context, 200, snapshot);
            return;
        }
        if (!HttpMethods.IsPost(context.Request.Method)) { await Error(context, 405, "use GET or POST"); return; }
        if (await AdminAccess.ReadBodyAsync(context) is not { } body) return;
        string action = body["action"]?.ToString() ?? "";
        if (action is "event_open" or "event_close" or "event_schedule" or "event_open_all" or "event_restore_all")
        {
            db.BeginTransaction();
            string? error = StudioEventEditor.Apply(db, context.RequestServices.GetRequiredService<TimeProvider>(), action, body);
            if (error is not null) { await Error(context, 400, error); return; }
            db.Commit();
            await AdminAccess.WriteAsync(context, 200, StudioEventEditor.Snapshot(db, context.RequestServices.GetRequiredService<TimeProvider>()));
            return;
        }
        if (action is "arena_time" or "arena_number" or "arena_end" or "arena_next" or "arena_ally")
        {
            long value = 0;
            if (action == "arena_time" && !Number(body, "seconds", 1, ArenaRules.WeekSeconds, out value))
            { await Error(context, 400, "remaining time must be between 1 second and 7 days"); return; }
            if (action == "arena_number" && !Number(body, "season", 1, 1_000_000, out value))
            { await Error(context, 400, "season must be between 1 and 1000000"); return; }
            if (action == "arena_ally" && (!Number(body, "featured_ally", 0, int.MaxValue, out value)
                || (value != 0 && !ArenaRules.LimitedAllyPairs.Any(pair => pair.SixStarId == value))))
            { await Error(context, 400, "unknown arena featured ally"); return; }
            db.BeginTransaction();
            var arena = context.RequestServices.GetRequiredService<ArenaService>();
            if (action == "arena_time") arena.SetRemaining(value);
            else if (action == "arena_number") arena.SetSeasonNumber(value);
            else if (action == "arena_ally") arena.SetFeaturedAlly(value);
            else if (action == "arena_end") arena.EndSeason();
            else arena.NextSeason();
            JsonObject status = ArenaStatus(arena);
            db.Commit();
            await AdminAccess.WriteAsync(context, 200, status);
            return;
        }
        if (action is "raid_time" or "raid_boss" or "raid_next")
        {
            long value = 0;
            if (action == "raid_time" && !Number(body, "seconds", 1, RaidRules.WeekSeconds, out value))
            { await Error(context, 400, "remaining time must be between 1 second and 7 days"); return; }
            if (action == "raid_boss" && (!Number(body, "stage_id", 1, int.MaxValue, out value) || !RaidRules.Bosses.Any(b => b.StageId == value)))
            { await Error(context, 400, "unknown raid boss"); return; }
            db.BeginTransaction();
            var raid = context.RequestServices.GetRequiredService<RaidService>();
            if (action == "raid_time") raid.SetRemaining(value);
            else raid.Advance(action == "raid_boss" ? value : null);
            RaidDungeonInfo current = raid.GetRaid();
            db.Commit();
            await AdminAccess.WriteAsync(context, 200, RaidStatus(current));
            return;
        }
        if (action == "mail_all")
        {
            if (body["items"] is not JsonArray rawItems || rawItems.Count is < 1 or > 50)
            { await Error(context, 400, "select between 1 and 50 items to mail"); return; }

            string title = (body["title"]?.ToString() ?? "").Trim();
            string message = (body["message"]?.ToString() ?? "").Trim();
            if (title.Length > 64) { await Error(context, 400, "mail title must be 64 characters or fewer"); return; }
            if (message.Length > 240) { await Error(context, 400, "mail description must be 240 characters or fewer"); return; }

            var master = context.RequestServices.GetRequiredService<MasterDataRepository>();
            HashSet<long> counted = master.GetEtcItemIds();
            HashSet<long> materials = master.GetMaterialItemIds();
            var rewards = new List<(long ItemId, long Count)>(rawItems.Count);
            foreach (JsonNode? node in rawItems)
            {
                if (node is not JsonObject entry
                    || !Number(entry, "item_id", 1, int.MaxValue, out long itemId)
                    || !Number(entry, "count", 1, int.MaxValue, out long count))
                { await Error(context, 400, "each mail item needs a valid item_id and positive count"); return; }

                bool single = master.FellowExists(itemId) || master.ItemExists(itemId) || materials.Contains(itemId);
                if ((!single && !counted.Contains(itemId)) || (single && count > 1000))
                { await Error(context, 400, $"unsupported mail item {itemId} or invalid count"); return; }
                rewards.Add((itemId, count));
            }

            long now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds();
            db.BeginTransaction();
            var players = db.Query<long>("SELECT user_no FROM gameuser WHERE user_no>0 ORDER BY user_no");
            var mail = context.RequestServices.GetRequiredService<MailRepository>();
            int mailsPerPlayer = (rewards.Count + 1) / 2;
            foreach (long playerNo in players)
            {
                for (int i = 0; i < rewards.Count; i += 2)
                {
                    (long id1, long val1) = rewards[i];
                    (long id2, long val2) = i + 1 < rewards.Count ? rewards[i + 1] : (0L, 0L);
                    mail.Add(playerNo, id1, val1, id2, val2, message: message, textCode: 0, type: 2, regDate: now, title: title);
                }
            }
            db.Commit();
            await AdminAccess.WriteAsync(context, 200, new JsonObject
            {
                ["ok"] = true,
                ["players"] = players.Count,
                ["mails_per_player"] = mailsPerPlayer,
                ["total_mails"] = players.Count * mailsPerPlayer,
            });
            return;
        }
        if (action == "chat_notice")
        {
            string message = (body["message"]?.ToString() ?? "").Trim();
            if (message.Length is < 1 or > 240)
            { await Error(context, 400, "notice message must be between 1 and 240 characters"); return; }

            int? targetGroup = null;
            if (body.ContainsKey("channel") && body["channel"] is not null && body["channel"]!.ToString() is { Length: > 0 } chRaw)
            {
                if (!int.TryParse(chRaw, out int ch) || ch < 0 || ch > 999)
                { await Error(context, 400, "channel must be 0 (all) or 1..999"); return; }
                if (ch > 0)
                {
                    targetGroup = 1000 + (ch - 1);
                }
            }

            long pinSeconds = 0;
            if (body["pin_seconds"] is not null && !Number(body, "pin_seconds", 0, 604800, out pinSeconds))
            { await Error(context, 400, "pin_seconds must be between 0 and 604800"); return; }

            if (pinSeconds > 0 && targetGroup is not null)
            { await Error(context, 400, "a pinned notice goes to every channel; choose all channels"); return; }
            if (pinSeconds > 0 && System.Text.Encoding.UTF8.GetByteCount(message) > ChatRelay.MaxPinBytes)
            { await Error(context, 400, $"a pinned notice must fit {ChatRelay.MaxPinBytes} bytes"); return; }

            bool sent = context.RequestServices.GetRequiredService<ChatRelay>().BroadcastNotice(message, targetGroup, pinSeconds);
            if (!sent) { await Error(context, 400, "notice message could not be broadcast"); return; }
            await AdminAccess.WriteAsync(context, 200, new JsonObject { ["ok"] = true });
            return;
        }
        if (action == "pins_list")
        {
            await AdminAccess.WriteAsync(context, 200, PinsReply(context.RequestServices.GetRequiredService<ChatRelay>()));
            return;
        }
        if (action is "pin_update" or "pin_remove")
        {
            ChatRelay relay = context.RequestServices.GetRequiredService<ChatRelay>();
            if (!Number(body, "id", 1, long.MaxValue, out long pinId)) { await Error(context, 400, "invalid pin id"); return; }
            bool changed;
            if (action == "pin_remove")
            {
                changed = relay.RemovePin(pinId);
            }
            else
            {
                string message = (body["message"]?.ToString() ?? "").Trim();
                if (message.Length is < 1 or > 240 || System.Text.Encoding.UTF8.GetByteCount(message) > ChatRelay.MaxPinBytes)
                { await Error(context, 400, "pinned notice must be 1 to 240 characters"); return; }
                if (!Number(body, "pin_seconds", 60, 604800, out long remaining))
                { await Error(context, 400, "pin_seconds must be between 60 and 604800"); return; }
                changed = relay.UpdatePin(pinId, message, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + remaining);
            }
            if (!changed) { await Error(context, 400, "pin not found or already ended"); return; }
            await AdminAccess.WriteAsync(context, 200, PinsReply(relay));
            return;
        }
        if (action == "notices_list")
        {
            await AdminAccess.WriteAsync(context, 200, NoticesReply(context.RequestServices.GetRequiredService<ChatRelay>()));
            return;
        }
        if (action is "notice_edit" or "notice_delete")
        {
            ChatRelay relay = context.RequestServices.GetRequiredService<ChatRelay>();
            var ids = new List<long>();
            if (body["ids"] is JsonArray idArray)
            {
                foreach (JsonNode? node in idArray)
                {
                    if (long.TryParse(node?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long id) && id > 0)
                    {
                        ids.Add(id);
                    }
                }
            }
            if (ids.Count is 0 or > 1000) { await Error(context, 400, "ids must list the notice rows"); return; }
            int changed;
            if (action == "notice_delete")
            {
                changed = relay.DeleteSavedNotice(ids);
            }
            else
            {
                string message = (body["message"]?.ToString() ?? "").Trim();
                if (message.Length is < 1 or > 240)
                { await Error(context, 400, "notice message must be between 1 and 240 characters"); return; }
                changed = relay.EditSavedNotice(ids, message);
            }
            if (changed == 0) { await Error(context, 400, "notice not found"); return; }
            JsonObject reply = NoticesReply(relay);
            reply["changed"] = changed;
            await AdminAccess.WriteAsync(context, 200, reply);
            return;
        }
        if (!Number(body, "user_no", 1, int.MaxValue, out long userNumber)) { await Error(context, 400, "invalid user_no"); return; }
        db.BeginTransaction();
        if (Find(db, userNumber) is null) { await Error(context, 404, "account not found"); return; }
        if (action == "profile")
        {
            string name = (body["nickname"]?.ToString() ?? "").Trim();
            if (!KnS.Platform.Accounts.PasswordAccountService.IsValidNickname(name)
                || !Number(body, "gold", 0, int.MaxValue, out long gold)
                || !Number(body, "gems", 0, int.MaxValue, out long gems)
                || !Number(body, "level", 1, int.MaxValue, out long level)
                || !Number(body, "experience", 0, int.MaxValue, out long experience)
                || !db.Exists("SELECT level FROM master_level WHERE level=@level AND exp_to_next>@experience", new { level, experience }))
            { await Error(context, 400, "invalid nickname, balance, level or experience"); return; }
            db.Execute("UPDATE gameuser SET nickname=@name,gold=@gold,cash=@gems,level=@level,exp=@experience WHERE user_no=@userNumber", new { name, gold, gems, level, experience, userNumber });
        }
        else if (action == "goods")
        {
            if (!Number(body, "item_id", 1900000, 1999999, out long itemId) || !Number(body, "count", 0, int.MaxValue, out long count)
                || itemId is CurrencyItems.Gold or CurrencyItems.Gems or CurrencyItems.Stamina
                || !db.Exists("SELECT item_id FROM master_etc_item WHERE item_id=@itemId", new { itemId }))
            { await Error(context, 400, "unknown counted item or invalid count"); return; }
            db.Execute($"{db.Dialect.InsertOrIgnore} INTO user_goods(user_no,item_id,count) VALUES(@userNumber,@itemId,0)", new { userNumber, itemId });
            db.Execute("UPDATE user_goods SET count=@count WHERE user_no=@userNumber AND item_id=@itemId", new { userNumber, itemId, count });
        }
        else if (action == "inventory_batch")
        {
            if (body["entries"] is not JsonArray entries || entries.Count is < 1 or > 100)
            { db.Rollback(); await Error(context, 400, "select between 1 and 100 entries"); return; }
            foreach (var node in entries)
            {
                string? error = "invalid batch entry";
                if (node is JsonObject entry)
                {
                    string kind = entry["action"]?.ToString() ?? "";
                    if (kind is "item" or "fellow" or "fellow_item" or "material")
                        error = StudioInventoryEditor.Grant(context.RequestServices, userNumber, kind, entry);
                    else if (kind is "item_delete" or "fellow_delete" or "fellow_item_delete")
                        error = StudioInventoryEditor.Apply(db, context.RequestServices, userNumber, kind, entry);
                }
                if (error is not null) { db.Rollback(); await Error(context, 400, error); return; }
            }
        }
        else if (action is "item" or "fellow" or "fellow_item" or "material")
        {
            string? error = StudioInventoryEditor.Grant(context.RequestServices, userNumber, action, body);
            if (error is not null) { await Error(context, 400, error); return; }
        }
        else if (action is "item_edit" or "item_delete" or "item_equip" or "fellow_edit" or "fellow_delete" or "fellow_slot" or "fellow_swap"
            or "fellow_item_edit" or "fellow_item_delete" or "fellow_item_equip")
        {
            string? error = StudioInventoryEditor.Apply(db, context.RequestServices, userNumber, action, body);
            if (error is not null) { await Error(context, 400, error); return; }
        }
        else { await Error(context, 400, "unknown action"); return; }
        db.Commit();
        await AdminAccess.WriteAsync(context, 200, new JsonObject { ["ok"] = true });
    }

    private static JsonObject ArenaStatus(ArenaService arena)
    {
        PvpControlRecord current = arena.CurrentSeason();
        ArenaLimitedAllyPair featured = ArenaService.FeaturedAlly(current);
        return new JsonObject
        {
            ["arena"] = new JsonObject
            {
                ["season"] = current.SeasonNumber,
                ["remain_time"] = arena.SecondsRemaining(current),
                ["closed"] = current.Closed != 0,
                ["featured_ally"] = featured.SixStarId,
                ["featured_ally_five_star"] = featured.FiveStarId,
                ["featured_ally_override"] = current.FeaturedAlly,
                ["featured_ally_name"] = featured.SixStarName,
                ["featured_ally_five_star_name"] = featured.FiveStarName,
            },
            ["arena_allies"] = JsonSerializer.SerializeToNode(ArenaRules.LimitedAllyPairs.Select(pair => new
            {
                six_star_id = pair.SixStarId,
                five_star_id = pair.FiveStarId,
                six_star_name = pair.SixStarName,
                five_star_name = pair.FiveStarName,
            })),
        };
    }

    private static JsonObject RaidStatus(RaidDungeonInfo current) => new()
    {
        ["raid"] = JsonSerializer.SerializeToNode(current),
        ["bosses"] = JsonSerializer.SerializeToNode(RaidRules.Bosses.Select(b => new { stage_id = b.StageId, name = b.MonsterFile.Replace("dt_st_raid_", "", StringComparison.Ordinal) })),
    };

    /// <summary>Notices saved in chat history (replayed on F5 and reconnect), newest first, one entry per send.</summary>
    private static JsonObject NoticesReply(ChatRelay relay) => new()
    {
        ["ok"] = true,
        ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["notices"] = new JsonArray([.. relay.SavedNotices().Select(n => (JsonNode)new JsonObject
        {
            ["ids"] = new JsonArray([.. n.Ids.Select(id => (JsonNode)id)]),
            ["groups"] = new JsonArray([.. n.Groups.Distinct().Order().Select(g => (JsonNode)g)]),
            ["created_at"] = n.CreatedAt, ["message"] = n.Text, ["sender"] = n.Sender,
            ["position"] = n.Position, ["end_time"] = n.EndTime, ["pin_id"] = n.PinId,
        })]),
    };

    /// <summary>Active pins, newest first; the game shows only the first.</summary>
    private static JsonObject PinsReply(ChatRelay relay) => new()
    {
        ["ok"] = true,
        ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["max_bytes"] = ChatRelay.MaxPinBytes,
        ["pins"] = new JsonArray([.. relay.ActivePins().Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id, ["message"] = p.Message, ["start_time"] = p.StartTime, ["end_time"] = p.EndTime,
        })]),
    };

    private static EditorAccount? Find(DataSession db, long user) => db.QueryFirstOrDefault<EditorAccount>(
        "SELECT user_no AS UserNumber, nickname AS Nickname, gold AS Gold, cash AS Gems, level AS Level, exp AS Experience FROM gameuser WHERE user_no=@user", new { user });
    private static bool Number(JsonObject body, string key, long min, long max, out long value) =>
        long.TryParse(body[key]?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    private static Task Error(HttpContext context, int status, string message) => AdminAccess.WriteAsync(context, status, new JsonObject { ["error"] = message });
}

public sealed record EditorAccount
{
    public long UserNumber { get; init; }
    public string? Nickname { get; init; }
    public long Gold { get; init; }
    public long Gems { get; init; }
    public long Level { get; init; }
    public long Experience { get; init; }
}
