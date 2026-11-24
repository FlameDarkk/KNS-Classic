using System.Text.Json.Nodes;
using KnS.Game;
using KnS.Game.Rewards;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public sealed class LoginRewardTests(ServerFixture server)
{
    private static long Today => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400;

    [Fact]
    public void Production_players_start_from_zero()
    {
        Assert.Equal(new StartingWallet(0, 0, 1, 0), StartingWallet.Fresh);
    }

    [Fact]
    public async Task First_login_pays_gift_and_calendar_into_mail_once_a_day()
    {
        long user = await PlayerAsync("login-first");
        JsonObject first = await server.CallGatewayAsync(67, new { }, user);
        Assert.Equal(0, Error(first));
        Assert.Equal(1, first["reward_type"]!.GetValue<long>());
        Assert.Equal(1, first["day"]!.GetValue<long>());
        JsonArray inbox = await InboxAsync(user);
        Assert.Equal(2, inbox.Count);
        Assert.Contains(inbox, mail => mail!["item_id1"]!.GetValue<long>() == 1900008 && mail["value1"]!.GetValue<long>() == 100_000);
        Assert.Contains(inbox, mail => mail!["item_id1"]!.GetValue<long>() == 1900014 && mail["value1"]!.GetValue<long>() == 20);

        // Titles come from native text table 14: "Returning User Event Day 1 Reward", "Day 1 Reward".
        Assert.Equal([9800101L, 9800201L], inbox.Select(mail => mail!["text_code"]!.GetValue<long>()).Order().ToArray());

        JsonObject again = await server.CallGatewayAsync(67, new { }, user);
        Assert.Equal(0, again["reward_type"]!.GetValue<long>());
        Assert.Equal(2, (await InboxAsync(user)).Count);
    }

    [Fact]
    public async Task Gift_runs_seven_login_days_then_calendar_shows()
    {
        long user = await PlayerAsync("login-week");
        for (long day = 1; day <= 7; day++)
        {
            JsonObject reply = await NextDayAsync(user);
            Assert.Equal(1, reply["reward_type"]!.GetValue<long>());
            Assert.Equal(day, reply["day"]!.GetValue<long>());
        }

        JsonObject eighth = await NextDayAsync(user);
        Assert.Equal(2, eighth["reward_type"]!.GetValue<long>());
        Assert.Equal(server.Scalar($"SELECT calendar_days FROM login_reward WHERE user_no={user}"), eighth["day"]!.GetValue<long>());
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM reward_mail WHERE user_no={user} AND item_id1=100504"));
    }

    [Fact]
    public async Task Gift_returns_only_after_thirty_days_away()
    {
        long user = await PlayerAsync("login-returning");
        server.Execute($"INSERT INTO login_reward(user_no,last_day,calendar_month,calendar_days,gift_day,gift_active,gift_used)"
            + $" VALUES({user},{Today - 29},0,0,7,0,1)");
        Assert.Equal(2, (await server.CallGatewayAsync(67, new { }, user))["reward_type"]!.GetValue<long>());
        server.Execute($"UPDATE login_reward SET last_day={Today - 30} WHERE user_no={user}");
        JsonObject back = await server.CallGatewayAsync(67, new { }, user);
        Assert.Equal(1, back["reward_type"]!.GetValue<long>());
        Assert.Equal(1, back["day"]!.GetValue<long>());
    }

    [Fact]
    public async Task Calendar_stops_after_28_days_and_restarts_next_month()
    {
        long user = await PlayerAsync("login-calendar");
        long month = DateTimeOffset.UtcNow.Year * 100 + DateTimeOffset.UtcNow.Month;
        server.Execute($"INSERT INTO login_reward(user_no,last_day,calendar_month,calendar_days,gift_day,gift_active,gift_used)"
            + $" VALUES({user},{Today - 1},{month},28,7,0,1)");
        Assert.Equal(0, (await server.CallGatewayAsync(67, new { }, user))["reward_type"]!.GetValue<long>());
        Assert.Empty(await InboxAsync(user));
        server.Execute($"UPDATE login_reward SET last_day={Today - 1}, calendar_month={month - 1} WHERE user_no={user}");
        JsonObject fresh = await server.CallGatewayAsync(67, new { }, user);
        Assert.Equal(2, fresh["reward_type"]!.GetValue<long>());
        Assert.Equal(1, fresh["day"]!.GetValue<long>());
    }

    [Fact]
    public async Task Every_login_reward_can_be_claimed_from_mail()
    {
        long user = await PlayerAsync("login-claim-all");
        server.Execute($"INSERT INTO reward_mail(user_no,item_id1,value1,item_id2,value2,message,text_code,type,reg_date)"
            + $" SELECT {user},item_id,value,0,0,'all',0,2,0 FROM master_login_reward");
        JsonArray inbox = await InboxAsync(user, 100);
        Assert.Equal(35, inbox.Count);
        long gold = server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");
        long[] numbers = inbox.Select(mail => mail!["mail_no"]!.GetValue<long>()).ToArray();
        JsonObject claimed = await server.CallGatewayAsync(115, new { mail_list = numbers }, user);
        Assert.Equal(0, Error(claimed));
        Assert.Empty(await InboxAsync(user));
        Assert.Equal(gold + 700_000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.Equal(6, claimed["fellow_info"]!.AsArray().Count);
        Assert.Equal(0, server.Scalar($"SELECT COALESCE(SUM(count),0) FROM user_goods WHERE user_no={user} AND item_id=1900019"));
        Assert.Equal(23, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user} AND item_id=1600002 AND claimed=1"));
    }

    [Fact]
    public async Task New_account_is_sent_the_first_tutorial_quest_and_finished_tutorial_stays_finished()
    {
        long user = await PlayerAsync("tutorial-chain");
        server.Execute($"UPDATE gameuser SET gold=0,cash=0 WHERE user_no={user}");
        Assert.Equal(5000001, await QuestAsync(user));

        // Advancing forward works; unknown or already-passed ids change nothing.
        Assert.Equal(0, Error(await server.CallGatewayAsync(602, new { quest_id = 5000003 }, user)));
        Assert.Equal(0, Error(await server.CallGatewayAsync(603, new { quest_id = 5100001 }, user)));
        Assert.Equal(5000004, await QuestAsync(user));
        Assert.Equal(0, Error(await server.CallGatewayAsync(603, new { quest_id = 5000002 }, user)));
        Assert.Equal(5000004, await QuestAsync(user));
        Assert.Equal(0, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));

        Assert.Equal(0, Error(await server.CallGatewayAsync(603, new { quest_id = 5000004 }, user)));
        Assert.Equal(5000005, await QuestAsync(user));
        Assert.Equal(0, Error(await server.CallGatewayAsync(603, new { quest_id = 5000005 }, user)));
        Assert.Equal(1000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        JsonObject list = await server.CallGatewayAsync(601, new { }, user);
        Assert.Empty(list["quest_infos"]!.AsArray());
        Assert.Empty(await InboxAsync(user));

        // Replaying a tutorial afterwards never reopens it or pays again.
        Assert.Equal(0, Error(await server.CallGatewayAsync(602, new { quest_id = 5000005 }, user)));
        Assert.Empty((await server.CallGatewayAsync(601, new { }, user))["quest_infos"]!.AsArray());
        Assert.Equal(1000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task Native_tutorial_chain_grants_30_gems_for_shop_draw_and_1000_gold_at_end_and_on_mission_unlock()
    {
        long user = await PlayerAsync("tutorial-native-flow");
        server.Execute($"UPDATE gameuser SET gold=0,cash=0 WHERE user_no={user}");
        Assert.Equal(5000001, await QuestAsync(user));

        // 5000001: First dungeon clear does NOT skip 5000002-5000005; scripted Lv.60 helper is sent.
        JsonObject start = await server.CallGatewayAsync(71, new { stage_id = 3000001, stage_mode = 1, friend_type = 2, friend_user_no = 1 }, user);
        Assert.Equal(0, Error(start));
        JsonObject helper = start["friend_detail_info"]!.AsObject();
        Assert.Equal(60, helper["user_level"]!.GetValue<long>());
        Assert.Equal(3, helper["hero_info"]!["hero_item_infos"]!.AsArray().Count);
        Assert.Equal(0, Error(await server.CallGatewayAsync(72, new { stage_no = 3000001, stage_result = 1, reward_gold = 0 }, user)));
        Assert.Equal(5000001, await QuestAsync(user));

        // Finish 5000001 -> advances to 5000002 (no currency reward).
        Assert.Equal(0, Error(await server.CallGatewayAsync(602, new { quest_id = 5000001 }, user)));
        Assert.Equal(5000002, await QuestAsync(user));
        Assert.Equal(0, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));

        // Finish 5000002 -> credits 30 Gems directly and advances to 5000003 (shop draw).
        JsonObject gemsReply = await server.CallGatewayAsync(602, new { quest_id = 5000002 }, user);
        Assert.Equal(0, Error(gemsReply));
        Assert.Equal(30, gemsReply["user_info"]!["user_cash"]!.GetValue<long>());
        Assert.Contains(gemsReply["update_item_etc_info"]!.AsArray(), item => item!["item_id"]!.GetValue<long>() == 1900009 && item["count"]!.GetValue<long>() == 30);
        Assert.Equal(5000003, await QuestAsync(user));

        // 5000003: Player spends those 30 Gems on Premium Ally Draw (9710006).
        JsonObject draw = await server.CallGatewayAsync(92, new { package_id = 9710006, use_ticket = 0, group_num = 1 }, user);
        Assert.Equal(0, Error(draw));
        Assert.Single(draw["fellow_info"]!.AsArray());
        Assert.Equal(0, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(0, Error(await server.CallGatewayAsync(602, new { quest_id = 5000003 }, user)));
        Assert.Equal(5000004, await QuestAsync(user));

        // 5000004 -> 5000005 -> +1,000 Gold, no custom Ally Ticket mail.
        long goldBeforeFinish = server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");
        Assert.Equal(0, Error(await server.CallGatewayAsync(602, new { quest_id = 5000004 }, user)));
        Assert.Equal(5000005, await QuestAsync(user));
        JsonObject finish = await server.CallGatewayAsync(603, new { quest_id = 5000005 }, user);
        Assert.Equal(0, Error(finish));
        Assert.Equal(goldBeforeFinish + 1000, finish["user_info"]!["user_gold"]!.GetValue<long>());
        Assert.Empty((await server.CallGatewayAsync(601, new { }, user))["quest_infos"]!.AsArray());
        Assert.Empty(await InboxAsync(user));

        // 5000012 (Lv.7 Mission unlock tutorial) grants 1,000 Gold once.
        JsonObject unlock = await server.CallGatewayAsync(602, new { quest_id = 5000012 }, user);
        Assert.Equal(0, Error(unlock));
        Assert.Equal(goldBeforeFinish + 2000, unlock["user_info"]!["user_gold"]!.GetValue<long>());
        Assert.Equal(0, Error(await server.CallGatewayAsync(602, new { quest_id = 5000012 }, user)));
        Assert.Equal(goldBeforeFinish + 2000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task A_legacy_player_past_stage_one_is_not_sent_into_the_tutorial_again()
    {
        long user = await PlayerAsync("tutorial-past");
        await server.CallGatewayAsync(71, new { stage_id = 3000002, stage_mode = 1, friend_type = 0, friend_user_no = 0 }, user);
        Assert.Equal(0, Error(await server.CallGatewayAsync(72, new { stage_no = 3000002, stage_result = 1, reward_gold = 0 }, user)));
        Assert.Empty((await server.CallGatewayAsync(601, new { }, user))["quest_infos"]!.AsArray());
        Assert.Empty(await InboxAsync(user));
    }

    [Fact]
    public async Task An_ally_ticket_pays_one_ally_draw()
    {
        long user = await PlayerAsync("tutorial-ticket-draw");
        server.Execute($"INSERT INTO user_goods(user_no,item_id,count) VALUES({user},1900012,1)");

        JsonObject draw = await server.CallGatewayAsync(92, new { package_id = 9710006, use_ticket = 1, group_num = 1 }, user);
        Assert.Equal(0, Error(draw));
        Assert.Single(draw["fellow_info"]!.AsArray());
        JsonNode level = Assert.Single(draw["update_achievement_info"]!.AsArray(), row => row!["type"]!.GetValue<long>() == 40000)!;
        Assert.Equal(40000, level["type"]!.GetValue<long>());
        Assert.True(level["count"]!.GetValue<long>() > 0);
        Assert.Equal(0, server.Scalar($"SELECT COALESCE(SUM(count),0) FROM user_goods WHERE user_no={user} AND item_id=1900012"));
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(92, new { package_id = 9710006, use_ticket = 1, group_num = 1 }, user)));
    }

    private async Task<long> QuestAsync(long user)
    {
        JsonObject list = await server.CallGatewayAsync(601, new { }, user);
        Assert.Equal(0, Error(list));
        JsonObject quest = list["quest_infos"]!.AsArray().Single()!.AsObject();
        Assert.Equal(0, quest["complete"]!.GetValue<long>());
        return quest["quest_id"]!.GetValue<long>();
    }

    [Fact]
    public async Task Mail_stored_with_old_text_codes_gets_native_titles()
    {
        long user = await PlayerAsync("mail-legacy-titles");
        server.Execute($"INSERT INTO reward_mail(user_no,item_id1,value1,item_id2,value2,message,text_code,type,reg_date)"
            + $" VALUES({user},1900009,5,0,0,'a',9991278,2,1),({user},1900009,5,0,0,'b',9993507,2,2),({user},1900009,5,0,0,'c',9992912,2,3)");
        long[] codes = (await InboxAsync(user)).Select(mail => mail!["text_code"]!.GetValue<long>()).Order().ToArray();
        Assert.Equal([9800011L, 9800022L, 9801001L], codes);
    }

    private async Task<JsonObject> NextDayAsync(long user)
    {
        server.Execute($"UPDATE login_reward SET last_day=last_day-1 WHERE user_no={user}");
        return await server.CallGatewayAsync(67, new { }, user);
    }

    private async Task<JsonArray> InboxAsync(long user, int count = 30) =>
        (await server.CallGatewayAsync(63, new { type = 2, page = 1, count }, user))["mail_info"]!.AsArray();

    private async Task<long> PlayerAsync(string name)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = name }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        return user;
    }

    private static long Error(JsonObject reply) => reply["errno"]!.GetValue<long>();
}
