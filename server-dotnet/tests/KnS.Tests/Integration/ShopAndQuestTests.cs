using System.Text.Json.Nodes;
using KnS.Data;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Randomness;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

/// <summary>The replies that were bare before: the client parsers read these keys and fault or loop without them.</summary>
[Collection(SharedServer.Name)]
public class ShopAndQuestTests(ServerFixture server)
{
    private static readonly long[] StarterSword = [1001];

    [Fact]
    public async Task Third_story_tracks_only_unlocked_target_kills_and_awards_browser_rewards_once()
    {
        long user = await NewPlayerAsync("third-story");
        long other = await NewPlayerAsync("third-story-other");
        long gold = Gold(user);
        JsonObject locked = await server.CallGatewayAsync(152, new { story_id = 7000003 }, user);
        Assert.NotEqual(0, Errno(locked));
        await ReportAsync(43005, 30);
        Assert.Equal(0, Progress());
        server.Execute($"INSERT INTO story_completion(user_no, story_id) VALUES({user},7000001),({user},7000002)");

        await ReportAsync(43002, 30);
        await ReportAsync(43005, -1);
        Assert.Equal(0, Progress());
        JsonObject partial = await ReportAsync(43005, 12);
        Assert.Equal(7000003, partial["story_info"]!["story_id"]!.GetValue<long>());
        Assert.Equal(12, partial["story_info"]!["quest1_count"]!.GetValue<long>());
        JsonObject early = await server.CallGatewayAsync(152, new { story_id = 7000003 }, user);
        Assert.NotEqual(0, Errno(early));
        Assert.Equal(gold, Gold(user));
        JsonObject reloaded = await server.CallGatewayAsync(151, new { }, user);
        Assert.Equal(12, reloaded["story_infos"]![2]!["quest1_count"]!.GetValue<long>());
        Assert.Single((await server.CallGatewayAsync(151, new { }, other))["story_infos"]!.AsArray());

        JsonObject malformed = await server.CallGatewayAsync(72, new
        {
            stage_no = 3000024, stage_result = 0, reward_gold = 0,
            achievement_list = new[] { new { type = 43005, count = "bad" } },
        }, user);
        Assert.Equal(12, malformed["story_info"]!["quest1_count"]!.GetValue<long>());
        server.Execute($"INSERT OR REPLACE INTO stage_start(user_no,stage_id) VALUES({user},3000024)");
        JsonObject finished = await server.CallGatewayAsync(72, new
        {
            stage_no = 3000024, stage_result = 0, reward_gold = 0,
            achievement_list = new[] { new { type = 43005, count = long.MaxValue }, new { type = 43005, count = 1L } },
        }, user);
        Assert.Equal(30, finished["story_info"]!["quest1_count"]!.GetValue<long>());
        Assert.Equal(30, Progress());
        JsonObject claim = await server.CallGatewayAsync(152, new { story_id = 7000003 }, user);
        Assert.Equal(0, Errno(claim));
        Assert.Equal(1, claim["complete_story_info"]!["complete"]!.GetValue<long>());
        Assert.Equal(30, claim["complete_story_info"]!["quest1_count"]!.GetValue<long>());
        Assert.Equal(7000004, claim["story_info"]!["story_id"]!.GetValue<long>());
        Assert.Equal(gold + 650, Gold(user));
        Assert.Equal(120, claim["user_info"]!["user_exp"]!.GetValue<long>());
        Assert.Equal(100030, Count(claim, 1900016));

        JsonObject duplicate = await server.CallGatewayAsync(152, new { story_id = 7000003 }, user);
        Assert.NotEqual(0, Errno(duplicate));
        Assert.Equal(gold + 650, Gold(user));
        Assert.Equal(100030, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900016"));
        JsonObject after = await server.CallGatewayAsync(151, new { }, user);
        Assert.Equal(4, after["story_infos"]!.AsArray().Count);
        Assert.Equal(1, after["story_infos"]![2]!["complete"]!.GetValue<long>());
        Assert.NotEqual(0, Errno(await server.CallGatewayAsync(152, new { story_id = 7000004 }, user)));
        Assert.Equal(7000004, (await ReportAsync(43005, 30))["story_info"]!["story_id"]!.GetValue<long>());
        Assert.Equal(30, Progress());

        long Progress() => server.Scalar($"SELECT COALESCE(SUM(quest_count),0) FROM story_progress WHERE user_no={user} AND story_id=7000003");
        Task<JsonObject> ReportAsync(long type, long count)
        {
            // A result must consume an attempt. Keep this story-only fixture free of rolled loot.
            server.Execute($"INSERT OR REPLACE INTO stage_start(user_no,stage_id) VALUES({user},3000024)");
            return server.CallGatewayAsync(72, new
            {
                stage_no = 3000024, stage_result = 0, reward_gold = 0,
                achievement_list = new[] { new { type, count } },
            }, user);
        }
    }

    [Fact]
    public async Task First_story_requires_steve_and_awards_once_then_opens_next_story()
    {
        long user = await NewPlayerAsync("first-story");
        JsonObject premature = await server.CallGatewayAsync(152, new { story_id = 7000001 }, user);
        Assert.NotEqual(0, Errno(premature));
        server.Execute($"INSERT INTO fellow_book(user_no, fellow_id) VALUES({user}, 100301)");
        JsonObject list = await server.CallGatewayAsync(151, new { }, user);
        Assert.Equal(1, list["story_infos"]![0]!["quest1_count"]!.GetValue<long>());

        JsonObject result = await server.CallGatewayAsync(152, new { story_id = 7000001 }, user);
        Assert.Equal(0, Errno(result));
        Assert.Equal(7000001, result["complete_story_info"]!["story_id"]!.GetValue<long>());
        Assert.Equal(1, result["complete_story_info"]!["complete"]!.GetValue<long>());
        Assert.Equal(7000002, result["story_info"]!["story_id"]!.GetValue<long>());
        Assert.Equal(100299, Gold(user));
        Assert.Equal(100, result["user_info"]!["user_exp"]!.GetValue<long>());

        JsonObject again = await server.CallGatewayAsync(152, new { story_id = 7000001 }, user);
        Assert.NotEqual(0, Errno(again));
        Assert.Equal(100299, Gold(user));
        Assert.Equal(2, (await server.CallGatewayAsync(151, new { }, user))["story_infos"]!.AsArray().Count);

        server.Execute($"INSERT OR REPLACE INTO stage_start(user_no,stage_id) VALUES({user},3000001)");
        JsonObject stage = await server.CallGatewayAsync(72, new
        {
            stage_no = 3000001, stage_result = 1, reward_gold = 0,
            achievement_list = new[] { new { type = 43002, count = 32 }, new { type = 43005, count = 50 } },
        }, user);
        Assert.Equal(30, stage["story_info"]!["quest1_count"]!.GetValue<long>());
        JsonObject second = await server.CallGatewayAsync(152, new { story_id = 7000002 }, user);
        Assert.Equal(0, Errno(second));
        Assert.Equal(7000003, second["story_info"]!["story_id"]!.GetValue<long>());
        Assert.Equal(100799, Gold(user));
        Assert.Equal(100020, Count(second, 1900016));
        Assert.NotEqual(0, Errno(await server.CallGatewayAsync(152, new { story_id = 7000002 }, user)));
    }

    [Fact]
    public async Task Raid_entry_progress_is_per_player_and_previous_days_do_not_count()
    {
        long user = await NewPlayerAsync("daily-raid-player");
        long other = await NewPlayerAsync("daily-raid-other");

        JsonObject start = await server.CallGatewayAsync(503, new { raid_no = 0 }, user);
        Assert.Equal(5300003, start["daily_quest_info"]!["quest_id"]!.GetValue<long>());
        Assert.Equal(1, start["daily_quest_info"]!["quest_count"]!.GetValue<long>());
        await server.CallGatewayAsync(503, new { raid_no = 0 }, user);
        JsonObject list = await server.CallGatewayAsync(141, new { }, user);
        Assert.InRange(list["remain_time"]!.GetValue<long>(), 1, 86400);
        JsonNode quest = list["daily_quest_infos"]!.AsArray().Single(row => row!["quest_id"]!.GetValue<long>() == 5300003)!;
        Assert.Equal(1, quest["quest_count"]!.GetValue<long>());
        Assert.Equal(0, quest["complete"]!.GetValue<long>());

        JsonObject otherList = await server.CallGatewayAsync(141, new { }, other);
        Assert.Equal(0, otherList["daily_quest_infos"]!.AsArray().Single(row => row!["quest_id"]!.GetValue<long>() == 5300003)!["quest_count"]!.GetValue<long>());
        server.Execute($"UPDATE daily_quest_progress SET day_no=day_no-1 WHERE user_no={user} AND quest_id=5300003");
        JsonObject nextDay = await server.CallGatewayAsync(141, new { }, user);
        Assert.Equal(0, nextDay["daily_quest_infos"]!.AsArray().Single(row => row!["quest_id"]!.GetValue<long>() == 5300003)!["quest_count"]!.GetValue<long>());
    }

    [Fact]
    public async Task Daily_quest_reward_can_be_claimed_only_once_after_progress()
    {
        long user = await NewPlayerAsync("daily-raid-claim");
        JsonObject early = await server.CallGatewayAsync(143, new { quest_id = 5300003 }, user);
        Assert.NotEqual(0, Errno(early));

        await server.CallGatewayAsync(503, new { raid_no = 0 }, user);
        JsonObject claim = await server.CallGatewayAsync(143, new { quest_id = 5300003 }, user);
        Assert.Equal(0, Errno(claim));
        Assert.Equal(1, claim["daily_quest_info"]!["complete"]!.GetValue<long>());
        Assert.Equal(1, claim["complete_daily_quest_info"]!["quest_count"]!.GetValue<long>());
        Assert.Equal(13, Count(claim, 1900017));

        JsonObject duplicate = await server.CallGatewayAsync(143, new { quest_id = 5300003 }, user);
        Assert.NotEqual(0, Errno(duplicate));
        JsonObject list = await server.CallGatewayAsync(141, new { }, user);
        Assert.Equal(1, list["daily_quest_infos"]!.AsArray().Single(row => row!["quest_id"]!.GetValue<long>() == 5300003)!["complete"]!.GetValue<long>());
    }

    private const long PendantOne = 9710106, PendantEleven = 9710105, AllyItemOne = 9710402, AllyItemEleven = 9710401;

    private async Task<long> NewPlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        return user;
    }

    private long Gems(long user) => server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}");

    private long Gold(long user) => server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");

    private static long Errno(JsonObject reply) => reply["errno"]?.GetValue<long>() ?? 0;

    private static long Count(JsonObject reply, long itemId) =>
        reply["update_item_etc_info"]!.AsArray().Single(row => row!["item_id"]!.GetValue<long>() == itemId)!["count"]!.GetValue<long>();

    [Fact]
    public async Task The_black_market_follows_late_game_gear_per_equipment_range()
    {
        long user = await NewPlayerAsync("black-market-late");
        server.Execute($"INSERT INTO item(user_no, item_id, claimed) VALUES({user}, 200111, 1)");
        var weapons = new HashSet<long>();
        var armor = new HashSet<long>();

        for (int refresh = 0; refresh < 30; refresh++)
        {
            server.Execute($"UPDATE gameuser SET cash=100 WHERE user_no={user}");
            JsonObject reply = await server.CallGatewayAsync(85, new { use_cash = 1 }, user);
            Assert.Equal(0, Errno(reply));
            JsonArray stock = reply["shop_info"]!.AsArray();
            Assert.Equal(9, stock.Count);
            foreach (JsonNode? offer in stock)
            {
                long id = offer!["item_id"]!.GetValue<long>();
                long grade = server.Scalar($"SELECT grade FROM master_item WHERE item_id={id}");
                (id / 100000 == 3 ? armor : weapons).Add(grade);
            }
        }

        // Weapon and shield lines reach grade 13; the owned grade-12 sword sets their window to 10-12. Armor's shop line ends
        // at grade 6, so it shows 4-6 instead of disappearing.
        Assert.Subset(new HashSet<long> { 10, 11, 12 }, weapons);
        Assert.Contains(12L, weapons);
        Assert.Subset(new HashSet<long> { 4, 5, 6 }, armor);
        Assert.NotEmpty(armor);
    }

    [Fact]
    public async Task A_new_players_black_market_starts_at_grade_one()
    {
        long user = await NewPlayerAsync("black-market-new");
        JsonObject reply = await server.CallGatewayAsync(85, new { use_cash = 0 }, user);
        foreach (JsonNode? offer in reply["shop_info"]!.AsArray())
        {
            long id = offer!["item_id"]!.GetValue<long>();
            Assert.InRange(server.Scalar($"SELECT grade FROM master_item WHERE item_id={id}"), 1, 1);
        }
    }

    [Fact]
    public async Task A_new_player_starts_at_level_10_with_hero_points()
    {
        long user = await NewPlayerAsync("defaults");

        JsonObject load = await server.CallGatewayAsync(60, new { user_no = user }, user);

        Assert.Equal(10, load["user_info"]!["user_level"]!.GetValue<long>());
        Assert.Contains(load["item_etc_info"]!.AsArray(),
            row => row!["item_id"]!.GetValue<long>() == 1900016 && row["count"]!.GetValue<long>() == 100_000);
    }

    [Theory]
    [InlineData(PendantOne, 1, 30)]
    [InlineData(PendantEleven, 11, 300)]
    public async Task A_pendant_draw_charges_gems_and_puts_pendants_of_grade_2_to_4_in_the_bag(long package, int draws, long price)
    {
        long user = await NewPlayerAsync("pendant" + package);
        long gems = Gems(user);

        JsonObject reply = await server.CallGatewayAsync(93, new { package_id = package, use_ticket = 0, group_num = -1 }, user);

        Assert.Equal(0, Errno(reply));
        JsonArray items = reply["item_infos"]!.AsArray();
        Assert.Equal(draws, items.Count);
        Assert.All(items, item => Assert.InRange(item!["item_id"]!.GetValue<long>(), 700000, 799999));
        Assert.Equal(gems - price, Gems(user));
        Assert.Equal(gems - price, Count(reply, 1900009));
        JsonObject bag = await server.CallGatewayAsync(61, new { user_no = user }, user);
        Assert.Contains(bag["item_infos"]!.AsArray(), item => item!["item_no"]!.GetValue<long>() == items[0]!["item_no"]!.GetValue<long>());
    }

    [Fact]
    public void Ally_draw_uses_grade_weights_before_selecting_a_character()
    {
        using IServiceScope scope = server.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        var random = new EachGradeRoll();
        var master = services.GetRequiredService<MasterDataRepository>();
        var service = new FellowService(services.GetRequiredService<FellowRepository>(),
            services.GetRequiredService<FellowItemRepository>(), master,
            services.GetRequiredService<FellowMissionRepository>(), random);
        Dictionary<long, long> grades = master.GetDrawableFellows().ToDictionary(f => f.FellowId, f => f.Grade);
        Dictionary<long, int> counts = Enumerable.Range(0, 100).Select(_ => grades[service.RollDrawnFellow()])
            .GroupBy(grade => grade).ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(30, counts[1]);
        Assert.Equal(25, counts[2]);
        Assert.Equal(20, counts[3]);
        Assert.Equal(13, counts[4]);
        Assert.Equal(8, counts[5]);
        Assert.Equal(4, counts[6]);
    }

    private sealed class EachGradeRoll : IRandomSource
    {
        private long _roll;
        private bool _grade = true;
        public long NextBelow(long exclusiveMax)
        {
            if (_grade)
            {
                Assert.Equal(100, exclusiveMax);
                _grade = false;
                return _roll++;
            }
            _grade = true;
            return 0;
        }
    }

    [Theory]
    [InlineData(9710005, 0, 11)]
    [InlineData(9710006, 0, 1)]
    [InlineData(9710005, 1, 11)]
    [InlineData(9710006, 1, 1)]
    public async Task Premium_draws_grant_and_persist_only_three_to_five_star_allies(long package, int ticket, int count)
    {
        long user = await NewPlayerAsync($"premium-{package}-{ticket}");
        server.Execute($"UPDATE gameuser SET cash=999999 WHERE user_no={user}");
        server.Execute($"INSERT INTO user_goods(user_no,item_id,count) VALUES({user},1900012,1000)"
            + " ON CONFLICT(user_no,item_id) DO UPDATE SET count=1000");
        for (int attempt = 0; attempt < 20; attempt++)
        {
            JsonObject reply = await server.CallGatewayAsync(92, new { package_id = package, use_ticket = ticket, group_num = -1 }, user);
            Assert.Equal(0, Errno(reply));
            JsonArray drawn = reply["fellow_info"]!.AsArray();
            Assert.Equal(count, drawn.Count);
            foreach (JsonNode? fellow in drawn)
            {
                long id = fellow!["fellow_id"]!.GetValue<long>();
                long number = fellow["fellow_no"]!.GetValue<long>();
                Assert.InRange(server.Scalar($"SELECT grade FROM master_fellow WHERE fellow_id={id}"), 3, 5);
                Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND rowid_alias={number - 7000} AND fellow_id={id}"));
            }
        }
    }

    [Theory]
    [InlineData(9710007, 11, 2000)]
    [InlineData(9710008, 1, 200)]
    public async Task Friendship_draws_charge_points_and_persist_only_one_to_three_star_allies(long package, int count, int price)
    {
        long user = await NewPlayerAsync($"friendship-draw-{package}");
        server.Execute($"INSERT INTO user_goods(user_no,item_id,count) VALUES({user},1900011,100000)"
            + " ON CONFLICT(user_no,item_id) DO UPDATE SET count=100000");
        for (int attempt = 0; attempt < 20; attempt++)
        {
            JsonObject reply = await server.CallGatewayAsync(92, new { package_id = package, use_ticket = 0, group_num = -1 }, user);
            Assert.Equal(0, Errno(reply));
            Assert.Equal(100000 - (attempt + 1) * price, Count(reply, 1900011));
            JsonArray drawn = reply["fellow_info"]!.AsArray();
            Assert.Equal(count, drawn.Count);
            foreach (JsonNode? fellow in drawn)
            {
                long id = fellow!["fellow_id"]!.GetValue<long>();
                long number = fellow["fellow_no"]!.GetValue<long>();
                Assert.InRange(server.Scalar($"SELECT grade FROM master_fellow WHERE fellow_id={id}"), 1, 3);
                Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND rowid_alias={number - 7000} AND fellow_id={id}"));
            }
        }
    }

    [Theory]
    [InlineData(9710005, 0, 11, 1900009, 300)]
    [InlineData(9710006, 1, 1, 1900012, 1)]
    public async Task Ally_draw_audit_preserves_player_payment_and_every_granted_instance(long package, int ticket, int count, long payment, long price)
    {
        long user = await NewPlayerAsync($"audit-{package}-{ticket}");
        server.Execute($"UPDATE gameuser SET cash=1000, nickname='AuditPlayer' WHERE user_no={user}");
        server.Execute($"INSERT INTO login_account(login_id,password_hash,did,guest_uid,nickname,created) SELECT 'audit-{package}-{ticket}','',hub_did,'','AuditPlayer',0 FROM gameuser WHERE user_no={user}");
        server.Execute($"INSERT INTO user_goods(user_no,item_id,count) VALUES({user},1900012,50) ON CONFLICT(user_no,item_id) DO UPDATE SET count=50");
        JsonObject reply = await server.CallGatewayAsync(92, new { package_id = package, use_ticket = ticket, group_num = -1 }, user);
        Assert.Equal(0, Errno(reply));
        JsonArray drawn = reply["fellow_info"]!.AsArray();
        Assert.Equal(count, drawn.Count);
        Assert.Equal(count, server.Scalar($"SELECT COUNT(*) FROM fellow_draw_audit WHERE user_no={user} AND username='audit-{package}-{ticket}' AND nickname='AuditPlayer' AND source='shop' AND package_id={package} AND payment_item={payment} AND payment_count={price} AND drawn_at>0"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(DISTINCT batch_id) FROM fellow_draw_audit WHERE user_no={user}"));
        foreach (JsonNode? ally in drawn)
        {
            long number = ally!["fellow_no"]!.GetValue<long>();
            long id = ally["fellow_id"]!.GetValue<long>();
            Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow_draw_audit a JOIN master_fellow m ON m.fellow_id=a.fellow_id WHERE a.user_no={user} AND a.fellow_no={number} AND a.fellow_id={id} AND a.grade=m.grade AND a.fellow_name=m.name"));
        }
    }

    [Fact]
    public async Task Ally_draw_audit_rolls_back_with_the_player_transaction()
    {
        long user = await NewPlayerAsync("audit-rollback");
        long gold = Gold(user);
        using (IServiceScope scope = server.CreateScope())
        {
            DataSession session = scope.ServiceProvider.GetRequiredService<DataSession>();
            session.BeginTransaction();
            session.Execute("UPDATE gameuser SET gold=gold-1 WHERE user_no=@user", new { user });
            scope.ServiceProvider.GetRequiredService<FellowDrawAuditRepository>().Record(user, "", "test", "rollback-test", 1, "shop", 9710006, 1900009, 30, 999999999, 100351);
            session.Rollback();
        }
        Assert.Equal(gold, Gold(user));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow_draw_audit WHERE user_no={user}"));
    }

    [Fact]
    public async Task Refused_ally_draw_creates_no_audit_rows()
    {
        long user = await NewPlayerAsync("audit-refused");
        server.Execute($"UPDATE gameuser SET cash=0 WHERE user_no={user}");
        Assert.NotEqual(0, Errno(await server.CallGatewayAsync(92, new { package_id = 9710006, use_ticket = 0 }, user)));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow_draw_audit WHERE user_no={user}"));
    }

    [Theory]
    [InlineData(9710201, 3, 500)]
    [InlineData(9710202, 4, 3000)]
    [InlineData(9710203, 5, 10000)]
    public async Task A_hero_point_draw_gives_its_grade_and_charges_hero_points(long package, long grade, long price)
    {
        long user = await NewPlayerAsync("heropoint" + package);

        JsonObject reply = await server.CallGatewayAsync(92, new { package_id = package, use_ticket = 0, group_num = -1 }, user);

        long fellowId = reply["fellow_info"]!.AsArray()[0]!["fellow_id"]!.GetValue<long>();
        Assert.Equal(grade, server.Scalar($"SELECT grade FROM master_fellow WHERE fellow_id={fellowId}"));
        Assert.Equal(100_000 - price, Count(reply, 1900016));
    }

    [Fact]
    public async Task A_pendant_draw_without_the_gems_is_refused_and_gives_nothing()
    {
        long user = await NewPlayerAsync("pendant-poor");
        server.Execute($"UPDATE gameuser SET cash=1 WHERE user_no={user}");

        JsonObject reply = await server.CallGatewayAsync(93, new { package_id = PendantOne, use_ticket = 0, group_num = -1 }, user);

        Assert.NotEqual(0, Errno(reply));
        Assert.Equal(1, Gems(user));
    }

    [Theory]
    [InlineData(AllyItemOne, 1)]
    [InlineData(AllyItemEleven, 11)]
    public async Task An_ally_item_draw_lists_the_same_items_for_the_roulette_and_keeps_them(long package, int draws)
    {
        long user = await NewPlayerAsync("allyitem" + package);

        JsonObject reply = await server.CallGatewayAsync(338, new { package_id = package }, user);

        JsonArray items = reply["fellow_item_infos"]!.AsArray();
        JsonArray lottery = reply["lottery_item_infos"]!.AsArray();
        Assert.Equal(draws, items.Count);
        Assert.Equal(draws, lottery.Count);
        Assert.Equal(items[0]!["fellow_item_no"]!.GetValue<long>(), lottery[0]!["fellow_item_no"]!.GetValue<long>());
        Assert.Equal(items[0]!["fellow_item_id"]!.GetValue<long>(), lottery[0]!["id"]!.GetValue<long>());
        JsonObject list = await server.CallGatewayAsync(330, new { }, user);
        Assert.Equal(draws, list["fellow_item_infos"]!.AsArray().Count);
    }

    private async Task<(long User, long Ally, long Weapon, long Ring1, long Ring2)> PlayerWithAllyAndGearAsync(string device)
    {
        long user = await NewPlayerAsync(device);
        server.Execute($"INSERT INTO fellow(user_no, fellow_id) VALUES({user}, 100001)");
        long ally = server.Scalar($"SELECT MAX(rowid_alias) FROM fellow WHERE user_no={user}") + 7000;
        long AddItem(long id)
        {
            server.Execute($"INSERT INTO fellow_item(user_no, fellow_item_id) VALUES({user}, {id})");
            return server.Scalar($"SELECT MAX(rowid_alias) FROM fellow_item WHERE user_no={user}") + 5_000_000;
        }

        return (user, ally, AddItem(1200001), AddItem(1300001), AddItem(1300003));
    }

    private static JsonObject AllyIn(JsonObject load, long ally) =>
        load["fellow_info"]!.AsArray().Single(row => row!["fellow_no"]!.GetValue<long>() == ally)!.AsObject();

    [Fact]
    public async Task Equipped_ally_gear_is_in_fellow_info_and_survives_a_login()
    {
        var (user, ally, weapon, ring1, ring2) = await PlayerWithAllyAndGearAsync("gear-equip");

        JsonObject reply = await server.CallGatewayAsync(333, new { fellow_no = ally, fellow_item_no = weapon, equip_index = 1 }, user);
        await server.CallGatewayAsync(333, new { fellow_no = ally, fellow_item_no = ring1, equip_index = 2 }, user);
        await server.CallGatewayAsync(333, new { fellow_no = ally, fellow_item_no = ring2, equip_index = 3 }, user);

        Assert.Equal(0, Errno(reply));
        Assert.Equal(weapon, reply["equip_fellow_item_no"]!.GetValue<long>());
        Assert.Equal(0, reply["unequip_fellow_item_no"]!.GetValue<long>());
        Assert.Equal(weapon, reply["fellow_info"]!["fellow_equip_weapon"]!.GetValue<long>());
        JsonObject info = AllyIn(await server.CallGatewayAsync(60, new { user_no = user }, user), ally);
        Assert.Equal(weapon, info["fellow_equip_weapon"]!.GetValue<long>());
        Assert.Equal(ring1, info["fellow_equip_accessory01"]!.GetValue<long>());
        Assert.Equal(ring2, info["fellow_equip_accessory02"]!.GetValue<long>());
        JsonArray bag = (await server.CallGatewayAsync(330, new { }, user))["fellow_item_infos"]!.AsArray();
        Assert.All(bag, item => Assert.Equal(ally, item!["fellow_no"]!.GetValue<long>()));
    }

    [Fact]
    public async Task Equipping_over_a_slot_frees_the_old_gear_and_no_item_takes_it_off()
    {
        var (user, ally, _, ring1, ring2) = await PlayerWithAllyAndGearAsync("gear-swap");
        await server.CallGatewayAsync(333, new { fellow_no = ally, fellow_item_no = ring1, equip_index = 2 }, user);

        JsonObject swap = await server.CallGatewayAsync(333, new { fellow_no = ally, fellow_item_no = ring2, equip_index = 2 }, user);
        JsonObject off = await server.CallGatewayAsync(333, new { fellow_no = ally, equip_index = 2 }, user);

        Assert.Equal(ring1, swap["unequip_fellow_item_no"]!.GetValue<long>());
        Assert.Equal(ring2, swap["fellow_info"]!["fellow_equip_accessory01"]!.GetValue<long>());
        Assert.Equal(ring2, off["unequip_fellow_item_no"]!.GetValue<long>());
        Assert.Equal(0, off["equip_fellow_item_no"]!.GetValue<long>());
        Assert.Equal(0, off["fellow_info"]!["fellow_equip_accessory01"]!.GetValue<long>());
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow_item WHERE user_no={user} AND fellow_no<>0"));
    }

    [Fact]
    public async Task Gear_in_the_wrong_slot_or_on_a_second_ally_is_refused()
    {
        var (user, ally, weapon, ring1, _) = await PlayerWithAllyAndGearAsync("gear-refuse");
        server.Execute($"INSERT INTO fellow(user_no, fellow_id) VALUES({user}, 100002)");
        long second = server.Scalar($"SELECT MAX(rowid_alias) FROM fellow WHERE user_no={user}") + 7000;
        await server.CallGatewayAsync(333, new { fellow_no = ally, fellow_item_no = ring1, equip_index = 2 }, user);

        Assert.NotEqual(0, Errno(await server.CallGatewayAsync(333, new { fellow_no = ally, fellow_item_no = weapon, equip_index = 2 }, user)));
        Assert.NotEqual(0, Errno(await server.CallGatewayAsync(333, new { fellow_no = ally, fellow_item_no = ring1, equip_index = 1 }, user)));
        Assert.NotEqual(0, Errno(await server.CallGatewayAsync(333, new { fellow_no = second, fellow_item_no = ring1, equip_index = 2 }, user)));
    }

    [Fact]
    public async Task Locking_ally_gear_answers_with_the_item_and_stays_after_a_login()
    {
        var (user, _, weapon, _, _) = await PlayerWithAllyAndGearAsync("gear-lock");

        JsonObject reply = await server.CallGatewayAsync(334, new { fellow_item_no = weapon, fellow_item_lock = 1 }, user);

        Assert.Equal(weapon, reply["fellow_item_no"]!.GetValue<long>());
        Assert.Equal(1, reply["fellow_item_lock"]!.GetValue<long>());
        JsonArray bag = (await server.CallGatewayAsync(330, new { }, user))["fellow_item_infos"]!.AsArray();
        Assert.Equal(1, bag.Single(item => item!["fellow_item_no"]!.GetValue<long>() == weapon)!["fellow_item_lock"]!.GetValue<long>());
        Assert.NotEqual(0, Errno(await server.CallGatewayAsync(334, new { fellow_item_no = 1, fellow_item_lock = 1 }, user)));
    }

    [Fact]
    public async Task Selling_ally_gear_pays_the_shown_price_and_skips_locked_and_worn_gear()
    {
        var (user, ally, weapon, ring1, ring2) = await PlayerWithAllyAndGearAsync("gear-sell");
        await server.CallGatewayAsync(334, new { fellow_item_no = weapon, fellow_item_lock = 1 }, user);
        await server.CallGatewayAsync(333, new { fellow_no = ally, fellow_item_no = ring1, equip_index = 2 }, user);
        long gold = Gold(user);

        JsonObject reply = await server.CallGatewayAsync(337, new { sell_fellow_item_no_list = new[] { weapon, ring1, ring2 } }, user);

        Assert.Equal([ring2], reply["success_list"]!.AsArray().Select(row => row!.GetValue<long>()));
        Assert.Equal([weapon, ring1], reply["fail_list"]!.AsArray().Select(row => row!.GetValue<long>()));
        Assert.Equal(gold + 280, Gold(user));
        JsonNode levelEntry = Assert.Single(reply["update_achievement_info"]!.AsArray(), row => row!["type"]!.GetValue<long>() == 40000)!;
        Assert.Equal(40000, levelEntry["type"]!.GetValue<long>());
    }

    [Fact]
    public async Task Selling_an_ally_puts_its_gear_back_in_the_bag()
    {
        var (user, ally, weapon, _, _) = await PlayerWithAllyAndGearAsync("gear-ally-sold");
        await server.CallGatewayAsync(333, new { fellow_no = ally, fellow_item_no = weapon, equip_index = 1 }, user);

        JsonObject reply = await server.CallGatewayAsync(306, new { sell_fellows = new[] { ally } }, user);

        Assert.Equal([weapon], reply["unequip_fellow_item_no_list"]!.AsArray().Select(row => row!.GetValue<long>()));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow_item WHERE user_no={user} AND fellow_no<>0"));
    }

    [Fact]
    public async Task The_black_market_keeps_its_stock_for_the_hour_and_a_paid_refresh_replaces_it()
    {
        long user = await NewPlayerAsync("market");
        long gems = Gems(user);

        JsonObject first = await server.CallGatewayAsync(85, new { use_cash = 0 }, user);
        JsonObject again = await server.CallGatewayAsync(85, new { use_cash = 0 }, user);
        JsonObject refreshed = await server.CallGatewayAsync(85, new { use_cash = 1 }, user);

        JsonArray stock = first["shop_info"]!.AsArray();
        Assert.Equal(9, stock.Count);
        Assert.Equal(stock.ToJsonString(), again["shop_info"]!.ToJsonString());
        Assert.NotEqual(stock.ToJsonString(), refreshed["shop_info"]!.ToJsonString());
        Assert.Equal(gems - 1, Gems(user));
        Assert.True(first["user_info"]!["user_next_shop_update_time"]!.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3000);
    }

    [Fact]
    public async Task Black_market_stock_follows_progress_and_skips_shop_specials()
    {
        long user = await NewPlayerAsync("market-progress");
        JsonArray fresh = (await server.CallGatewayAsync(85, new { use_cash = 0 }, user))["shop_info"]!.AsArray();
        Assert.NotEmpty(fresh);
        Assert.All(fresh, offer => Assert.Equal(1, GradeOf(offer!)));
        Assert.All(fresh, offer => Assert.Equal(0, server.Scalar($"SELECT c31 FROM master_item WHERE item_id={offer!["item_id"]!.GetValue<long>()}")));

        // Clearing an area whose stages drop grade 5 gear moves the window to grades 3-5.
        long stage = server.Scalar("SELECT r.stage_id FROM master_stage_reward r JOIN master_item i ON i.item_id=r.item_id"
            + " GROUP BY r.stage_id HAVING MAX(i.grade)=5 ORDER BY r.stage_id LIMIT 1");
        server.Execute($"INSERT INTO stage_clear(user_no,stage_id,stage_mode,clear_count) VALUES({user},{stage},1,1)");
        long cap = Math.Min(6, Math.Max(5, server.Scalar($"SELECT COALESCE(MAX(grade_max),0) FROM master_stage_random_reward WHERE stage_id={stage}")));
        JsonArray later = (await server.CallGatewayAsync(85, new { use_cash = 1 }, user))["shop_info"]!.AsArray();
        Assert.NotEmpty(later);
        Assert.All(later, offer => Assert.InRange(GradeOf(offer!), cap - 2, cap));
    }

    [Fact]
    public async Task Ally_draws_never_give_limited_or_altar_only_allies()
    {
        long user = await NewPlayerAsync("draw-no-limited");
        long package = server.Scalar("SELECT package_id FROM master_shop_package WHERE draw_count >= 10 ORDER BY package_id LIMIT 1");
        long priceItem = server.Scalar($"SELECT price_item_id FROM master_shop_package WHERE package_id={package}");
        server.Execute($"UPDATE gameuser SET gold=999999999, cash=999999 WHERE user_no={user}");
        server.Execute($"INSERT INTO user_goods(user_no,item_id,count) VALUES({user},{priceItem},999999)"
            + " ON CONFLICT(user_no,item_id) DO UPDATE SET count=999999");
        for (int i = 0; i < 10; i++)
            Assert.Equal(0, Errno(await server.CallGatewayAsync(92, new { package_id = package, use_ticket = 0, group_num = -1 }, user)));
        // Limited (int 92), Altar-only results and angels: the native draw pool (int 93) leaves them out.
        long[] excluded = [100486, 100499, 100502, 100506, 100512, 100520, 100522, 105012, 105015, 105017,
            100405, 100485, 100498, 100501, 100511, 100519, 100521, 100524, 105014, 105016, 105001, 105019, 110001];
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id IN ({string.Join(",", excluded)})"));
        Assert.True(server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user}") >= 100);
    }

    private long GradeOf(JsonNode offer) => server.Scalar($"SELECT grade FROM master_item WHERE item_id={offer["item_id"]!.GetValue<long>()}");

    [Fact]
    public async Task Buying_a_black_market_offer_charges_its_price_and_moves_it_to_the_bag_once()
    {
        long user = await NewPlayerAsync("market-buy");
        JsonObject list = await server.CallGatewayAsync(85, new { use_cash = 0 }, user);
        JsonNode offer = list["shop_info"]!.AsArray()[0]!;
        long itemNumber = offer["item_no"]!.GetValue<long>();
        server.Execute($"UPDATE gameuser SET gold=9000000 WHERE user_no={user}");
        long price = server.Scalar($"SELECT buy_price FROM master_item WHERE item_id={offer["item_id"]!.GetValue<long>()}");

        JsonObject bought = await server.CallGatewayAsync(87, new { item_no = itemNumber }, user);
        JsonObject twice = await server.CallGatewayAsync(87, new { item_no = itemNumber }, user);

        Assert.Equal(itemNumber, bought["item_info"]!["item_no"]!.GetValue<long>());
        Assert.Equal(9000000 - price, Gold(user));
        Assert.NotEqual(0, Errno(twice));
        JsonObject listAfter = await server.CallGatewayAsync(85, new { use_cash = 0 }, user);
        Assert.Equal(8, listAfter["shop_info"]!.AsArray().Count);
    }

    [Fact]
    public async Task Black_market_offers_are_not_in_the_bag_and_survive_a_stage_start()
    {
        long user = await NewPlayerAsync("market-stage");
        await server.CallGatewayAsync(85, new { use_cash = 0 }, user);

        JsonObject bag = await server.CallGatewayAsync(61, new { user_no = user }, user);
        await server.CallGatewayAsync(71, new { stage_id = 3000001, stage_mode = 1, friend_type = 0, friend_user_no = 0 }, user);
        JsonObject list = await server.CallGatewayAsync(85, new { use_cash = 0 }, user);

        Assert.DoesNotContain(bag["item_infos"]!.AsArray(), item => item!["item_no"]!.GetValue<long>() >= 2000 && item["hero_no"]!.GetValue<long>() == 0);
        Assert.Equal(9, list["shop_info"]!.AsArray().Count);
    }

    [Fact]
    public async Task Daily_quests_are_never_empty_because_an_empty_list_makes_the_client_loop_on_140()
    {
        JsonObject reply = await server.CallGatewayAsync(141, new { });

        Assert.Equal(8, reply["daily_quest_infos"]!.AsArray().Count);
    }

    [Fact]
    public async Task The_story_list_opens_the_first_story()
    {
        JsonObject reply = await server.CallGatewayAsync(151, new { });

        Assert.Equal(7000001, reply["story_infos"]!.AsArray()[0]!["story_id"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_stage_start_costs_its_hunger_and_an_empty_bar_refuses()
    {
        long user = await NewPlayerAsync("hunger");

        JsonObject start = await server.CallGatewayAsync(71, new { stage_id = 3000001, stage_mode = 1, friend_type = 0, friend_user_no = 0 }, user);

        Assert.Equal(29, start["user_info"]!["user_stamina"]!.GetValue<long>());
        Assert.Equal(29, Count(start, 1900014));
        Assert.InRange(start["user_info"]!["user_remind_time_stamina"]!.GetValue<long>(), 1, 300);

        server.Execute($"UPDATE player_stamina SET stamina=1 WHERE user_no={user}");
        JsonObject refused = await server.CallGatewayAsync(71, new { stage_id = 3000001, stage_mode = 1, friend_type = 0, friend_user_no = 0 }, user);
        Assert.NotEqual(0, Errno(refused));
    }

    [Fact]
    public async Task Legacy_stamina_goods_are_credited_once_and_retained_for_audit()
    {
        long user = await NewPlayerAsync("legacy-stamina"), other = await NewPlayerAsync("legacy-stamina-other");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        server.Execute($"INSERT INTO player_stamina(user_no,stamina,last_gain) VALUES({user},40,{now})");
        server.Execute($"INSERT INTO user_goods(user_no,item_id,count) VALUES({user},1900014,15)");
        server.Execute($"INSERT INTO user_goods(user_no,item_id,count) VALUES({other},1900014,7)");
        JsonObject first = await server.CallGatewayAsync(60, new { user_no = user }, user);
        Assert.Equal(55, first["user_info"]!["user_stamina"]!.GetValue<long>());
        Assert.Equal(55, first["item_etc_info"]!.AsArray().Single(row => row!["item_id"]!.GetValue<long>() == 1900014)!["count"]!.GetValue<long>());
        Assert.Equal(15, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900014"));
        Assert.Equal(15, server.Scalar($"SELECT credited FROM stamina_recovery WHERE user_no={user}"));
        Assert.Equal(7, server.Scalar($"SELECT count FROM user_goods WHERE user_no={other} AND item_id=1900014"));
        JsonObject repeat = await server.CallGatewayAsync(60, new { user_no = user }, user);
        Assert.Equal(55, repeat["user_info"]!["user_stamina"]!.GetValue<long>());
        server.Execute($"UPDATE user_goods SET count=20 WHERE user_no={user} AND item_id=1900014");
        JsonObject increased = await server.CallGatewayAsync(60, new { user_no = user }, user);
        Assert.Equal(60, increased["user_info"]!["user_stamina"]!.GetValue<long>());
        Assert.Equal(20, server.Scalar($"SELECT credited FROM stamina_recovery WHERE user_no={user}"));
    }

    [Fact]
    public async Task Concurrent_profile_loads_credit_legacy_stamina_only_once()
    {
        long user = await NewPlayerAsync("legacy-stamina-concurrent");
        server.Execute($"INSERT INTO user_goods(user_no,item_id,count) VALUES({user},1900014,10)");
        JsonObject[] replies = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => server.CallGatewayAsync(60, new { user_no = user }, user)));
        Assert.All(replies, reply => Assert.Equal(42, reply["user_info"]!["user_stamina"]!.GetValue<long>()));
        Assert.Equal(42, server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}"));
        Assert.Equal(10, server.Scalar($"SELECT credited FROM stamina_recovery WHERE user_no={user}"));
        Assert.Equal(10, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900014"));
    }

    [Fact]
    public async Task Hunger_regrows_a_point_every_five_minutes_up_to_full()
    {
        long user = await NewPlayerAsync("hunger-regrow");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await server.CallGatewayAsync(71, new { stage_id = 3000001, stage_mode = 1, friend_type = 0, friend_user_no = 0 }, user);

        server.Execute($"UPDATE player_stamina SET stamina=10, last_gain={now - 610} WHERE user_no={user}");
        JsonObject partly = await server.CallGatewayAsync(60, new { user_no = user }, user);
        server.Execute($"UPDATE player_stamina SET stamina=10, last_gain={now - 99_000} WHERE user_no={user}");
        JsonObject full = await server.CallGatewayAsync(60, new { user_no = user }, user);

        Assert.Equal(12, partly["user_info"]!["user_stamina"]!.GetValue<long>());
        Assert.Equal(32, full["user_info"]!["user_stamina"]!.GetValue<long>());
    }

    [Fact]
    public async Task Power_up_persists_option_gains_shown_in_preview()
    {
        long user = await NewPlayerAsync("item-option-power-up");
        server.Execute($"UPDATE gameuser SET gold=1000000 WHERE user_no={user}");
        server.Execute($"INSERT INTO item(user_no,item_id,claimed,defence,hp) VALUES({user},800041,1,5,30)");
        long target = server.Scalar($"SELECT MAX(rowid_alias) FROM item WHERE user_no={user}") + 2000;
        server.Execute($"INSERT INTO item_option(item_no,slot,user_no,option_id,value1,value2) VALUES({target},0,{user},2010402,93,0)");
        server.Execute($"INSERT INTO item_option(item_no,slot,user_no,option_id,value1,value2) VALUES({target},1,{user},2021203,50,7)");
        for (int level = 1; level <= 2; level++)
        {
            server.Execute($"INSERT INTO item(user_no,item_id,claimed) VALUES({user},800041,1)");
            long material = server.Scalar($"SELECT MAX(rowid_alias) FROM item WHERE user_no={user}") + 2000;
            JsonObject reply = await server.CallGatewayAsync(81, new { item_no = target, used_item_no = material }, user);
            Assert.Equal(0, Errno(reply));
            Assert.Equal(93 + 10 * level, reply["item_info"]!["item_options"]![0]!["item_option_value1"]!.GetValue<long>());
            Assert.Equal(93 + 10 * level, server.Scalar($"SELECT value1 FROM item_option WHERE item_no={target} AND slot=0"));
            Assert.Equal(50, server.Scalar($"SELECT value1 FROM item_option WHERE item_no={target} AND slot=1"));
            Assert.Equal(7 + 20 * level, server.Scalar($"SELECT value2 FROM item_option WHERE item_no={target} AND slot=1"));
        }
    }

    [Fact]
    public async Task Locked_power_up_materials_are_never_consumed_or_charged()
    {
        long user = await NewPlayerAsync("locked-item-material");
        server.Execute($"UPDATE gameuser SET gold=5000 WHERE user_no={user}");
        server.Execute($"INSERT INTO item(user_no,item_id,claimed,attack) VALUES({user},200001,1,17)");
        long target = server.Scalar($"SELECT MAX(rowid_alias) FROM item WHERE user_no={user}") + 2000;
        server.Execute($"INSERT INTO item(user_no,item_id,claimed,attack) VALUES({user},200001,1,17)");
        long material = server.Scalar($"SELECT MAX(rowid_alias) FROM item WHERE user_no={user}") + 2000;
        await server.CallGatewayAsync(80, new { hero_no = 1, item_no = target }, user);
        await server.CallGatewayAsync(84, new { item_no = material, item_lock = 1 }, user);
        await server.CallGatewayAsync(84, new { item_no = 1001, item_lock = 1 }, user);
        Assert.NotEqual(0, Errno(await server.CallGatewayAsync(81, new { item_no = target, used_item_no = material }, user)));
        Assert.NotEqual(0, Errno(await server.CallGatewayAsync(81, new { item_no = target, used_item_no = 1001 }, user)));
        Assert.Equal(5000, Gold(user));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user} AND rowid_alias={material - 2000}"));
        Assert.Equal(0, server.Scalar($"SELECT upgrade FROM item WHERE user_no={user} AND rowid_alias={target - 2000}"));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM starter_sold WHERE user_no={user} AND item_no=1001"));
        await server.CallGatewayAsync(84, new { item_no = material, item_lock = 0 }, user);
        Assert.Equal(0, Errno(await server.CallGatewayAsync(81, new { item_no = target, used_item_no = material }, user)));
        Assert.Equal(4500, Gold(user));
    }

    [Fact]
    public async Task Enhance_refuses_locked_or_duplicate_material_instances()
    {
        long user = await NewPlayerAsync("enhance-material-safety");
        server.Execute($"UPDATE gameuser SET gold=5000 WHERE user_no={user}");
        server.Execute($"INSERT INTO item(user_no,item_id,claimed,upgrade) VALUES({user},200001,1,2)");
        long target = server.Scalar($"SELECT MAX(rowid_alias) FROM item WHERE user_no={user}") + 2000;
        server.Execute($"INSERT INTO item(user_no,item_id,claimed,locked) VALUES({user},1701001,1,1)");
        long material = server.Scalar($"SELECT MAX(rowid_alias) FROM item WHERE user_no={user}") + 2000;
        JsonObject locked = await server.CallGatewayAsync(82, new { item_no = target, used_item_no_list = new[] { material } }, user);
        Assert.NotEqual(0, Errno(locked));
        Assert.Equal(5000, Gold(user));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user} AND rowid_alias={material - 2000}"));
        server.Execute($"UPDATE item SET item_id=200011,upgrade=5 WHERE user_no={user} AND rowid_alias={target - 2000}");
        server.Execute($"UPDATE item SET locked=0 WHERE user_no={user} AND rowid_alias={material - 2000}");
        JsonObject repeated = await server.CallGatewayAsync(82, new { item_no = target, used_item_no_list = new[] { material, material, material } }, user);
        Assert.NotEqual(0, Errno(repeated));
        Assert.Equal(5000, Gold(user));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user} AND rowid_alias={material - 2000}"));
        Assert.Equal(200011, server.Scalar($"SELECT item_id FROM item WHERE user_no={user} AND rowid_alias={target - 2000}"));
        server.Execute($"INSERT INTO item(user_no,item_id,claimed) VALUES({user},1701001,1)");
        long second = server.Scalar($"SELECT MAX(rowid_alias) FROM item WHERE user_no={user}") + 2000;
        server.Execute($"INSERT INTO item(user_no,item_id,claimed) VALUES({user},1701001,1)");
        long third = server.Scalar($"SELECT MAX(rowid_alias) FROM item WHERE user_no={user}") + 2000;
        JsonObject valid = await server.CallGatewayAsync(82, new { item_no = target, used_item_no_list = new[] { material, second, third } }, user);
        Assert.Equal(0, Errno(valid));
        Assert.Equal(4000, Gold(user));
        Assert.Equal(200021, server.Scalar($"SELECT item_id FROM item WHERE user_no={user} AND rowid_alias={target - 2000}"));
    }

    [Fact]
    public async Task Locked_starter_gear_cannot_be_sold()
    {
        long user = await NewPlayerAsync("sell-locked-starter");
        server.Execute($"INSERT INTO item(user_no,item_id,claimed) VALUES({user},200001,1)");
        long replacement = server.Scalar($"SELECT MAX(rowid_alias) FROM item WHERE user_no={user}") + 2000;
        await server.CallGatewayAsync(80, new { hero_no = 1, item_no = replacement }, user);
        await server.CallGatewayAsync(84, new { item_no = 1001, item_lock = 1 }, user);
        long gold = Gold(user);
        JsonObject reply = await server.CallGatewayAsync(83, new { sell_items = StarterSword }, user);
        Assert.Empty(reply["success_list"]!.AsArray());
        Assert.Single(reply["fail_list"]!.AsArray());
        Assert.Equal(gold, Gold(user));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM starter_sold WHERE user_no={user} AND item_no=1001"));
    }

    [Fact]
    public async Task Selling_units_of_a_material_stack_pays_the_price_the_client_shows()
    {
        long user = await NewPlayerAsync("sell-material-units");
        for (int i = 0; i < 3; i++) server.Execute($"INSERT INTO item(user_no,item_id,claimed) VALUES({user},1701001,1)");
        var numbers = new List<long>();
        for (long row = server.Scalar($"SELECT MAX(rowid_alias) FROM item WHERE user_no={user}") - 2; row <= server.Scalar($"SELECT MAX(rowid_alias) FROM item WHERE user_no={user}"); row++)
            numbers.Add(row + 2000);
        long gold = Gold(user);
        long price = server.Scalar("SELECT c4 FROM master_material WHERE item_id=1701001");

        JsonObject reply = await server.CallGatewayAsync(83, new { sell_items = numbers.Take(2).ToArray() }, user);

        Assert.Equal(2, reply["success_list"]!.AsArray().Count);
        Assert.Equal(gold + 2 * price, Gold(user));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user} AND item_id=1701001"));
    }

    [Fact]
    public async Task Starter_gear_locks_and_stays_locked_across_a_login()
    {
        const long StarterItem = 1001;
        long user = await NewPlayerAsync("starter-lock");

        JsonObject locked = await server.CallGatewayAsync(84, new { item_no = StarterItem, item_lock = 1 }, user);
        JsonObject bag = await server.CallGatewayAsync(61, new { user_no = user }, user);
        JsonObject unlocked = await server.CallGatewayAsync(84, new { item_no = StarterItem, item_lock = 0 }, user);

        Assert.Equal(1, locked["item_lock"]!.GetValue<long>());
        Assert.Equal(1, bag["item_infos"]!.AsArray().Single(item => item!["item_no"]!.GetValue<long>() == StarterItem)!["item_lock"]!.GetValue<long>());
        Assert.Equal(0, unlocked["item_lock"]!.GetValue<long>());
    }
}
