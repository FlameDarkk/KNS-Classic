using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

/// <summary>Friends, ally missions, Arena and Raid: the replies the client screens need.</summary>
[Collection(SharedServer.Name)]
public class SocialTests(ServerFixture server)
{
    private async Task<long> NewPlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        return user;
    }

    private static long Errno(JsonObject reply) => reply["errno"]?.GetValue<long>() ?? 0;

    private static IEnumerable<long> Numbers(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<long>());

    [Fact]
    public async Task A_friend_request_is_accepted_listed_and_deleted()
    {
        long a = await NewPlayerAsync("friend-a"), b = await NewPlayerAsync("friend-b");

        JsonObject sent = await server.CallGatewayAsync(801, new { friend_list = new[] { b } }, a);
        Assert.Equal([b], Numbers(sent["success_list"]));

        JsonObject incoming = await server.CallGatewayAsync(113, new { }, b);
        Assert.Equal(a, incoming["friend_request_user_info"]![0]!["user_no"]!.GetValue<long>());

        JsonObject accepted = await server.CallGatewayAsync(111, new { friend_list = new[] { a } }, b);
        Assert.Equal(a, accepted["friend_info"]![0]!["user_no"]!.GetValue<long>());

        JsonObject listA = await server.CallGatewayAsync(803, new { page = 1, count = 50 }, a);
        Assert.Equal(b, listA["friend_info"]![0]!["user_no"]!.GetValue<long>());

        JsonObject detail = await server.CallGatewayAsync(64, new { friend_user_no = b, type = 4 }, a);
        Assert.Equal(b, detail["user_no"]!.GetValue<long>());
        Assert.Equal(b, detail["hero_info"]!["user_no"]!.GetValue<long>());

        await server.CallGatewayAsync(112, new { friend_user_no = b }, a);
        Assert.Empty((await server.CallGatewayAsync(803, new { page = 1, count = 50 }, b))["friend_info"]!.AsArray());
    }

    [Fact]
    public async Task Friendship_points_can_be_sent_once_a_day_and_pay_the_sender()
    {
        long a = await NewPlayerAsync("points-a"), b = await NewPlayerAsync("points-b");
        await server.CallGatewayAsync(801, new { friend_list = new[] { b } }, a);
        await server.CallGatewayAsync(111, new { friend_list = new[] { a } }, b);

        JsonObject first = await server.CallGatewayAsync(116, new { friend_list = new[] { b } }, a);
        JsonObject repeat = await server.CallGatewayAsync(116, new { friend_list = new[] { b } }, a);
        Assert.Equal(1, first["daily_quest_info"]!["quest_count"]!.GetValue<long>());
        Assert.Equal(1, repeat["daily_quest_info"]!["quest_count"]!.GetValue<long>());

        Assert.Equal(b, first["friend_info"]![0]!["user_no"]!.GetValue<long>());
        Assert.InRange(first["friend_info"]![0]!["send_friendship_point_remain_time"]!.GetValue<long>(), 86390, 86400);
        Assert.InRange(repeat["friend_info"]![0]!["send_friendship_point_remain_time"]!.GetValue<long>(), 86390, 86400);
        JsonObject refreshed = await server.CallGatewayAsync(803, new { page = 1, count = 50 }, a);
        Assert.Equal(first["friend_info"]![0]!["user_no"]!.GetValue<long>(), refreshed["friend_info"]![0]!["user_no"]!.GetValue<long>());

        Assert.Equal(10, server.Scalar($"SELECT count FROM user_goods WHERE user_no={a} AND item_id=1900011"));
        Assert.Equal(0, server.Scalar($"SELECT COALESCE(SUM(count),0) FROM user_goods WHERE user_no={b} AND item_id=1900011"));
    }

    [Fact]
    public async Task Sent_points_arrive_in_the_friends_FP_tab_and_can_be_claimed()
    {
        long a = await NewPlayerAsync("fp-tab-a"), b = await NewPlayerAsync("fp-tab-b");
        await server.CallGatewayAsync(801, new { friend_list = new[] { b } }, a);
        await server.CallGatewayAsync(111, new { friend_list = new[] { a } }, b);
        await server.CallGatewayAsync(116, new { friend_list = new[] { b } }, a);

        JsonObject tab = await server.CallGatewayAsync(63, new { type = 1, page = 1, count = 30 }, b);
        Assert.Equal(1, tab["total_mail_count"]!.GetValue<long>());
        JsonNode mail = tab["mail_info"]![0]!;
        Assert.Equal(1900011, mail["item_id1"]!.GetValue<long>());
        Assert.Equal(10, mail["value1"]!.GetValue<long>());
        Assert.Equal(0, (await server.CallGatewayAsync(63, new { type = 2, page = 1, count = 30 }, b))["total_mail_count"]!.GetValue<long>());

        JsonObject claim = await server.CallGatewayAsync(115, new { mail_list = new[] { mail["mail_no"]!.GetValue<long>() } }, b);
        Assert.Equal(0, claim["errno"]!.GetValue<long>());
        Assert.Equal(10, server.Scalar($"SELECT count FROM user_goods WHERE user_no={b} AND item_id=1900011"));
        Assert.Equal(0, (await server.CallGatewayAsync(63, new { type = 1, page = 1, count = 30 }, b))["total_mail_count"]!.GetValue<long>());
    }

    [Fact]
    public async Task Using_a_friend_as_dungeon_helper_mails_them_points_once_a_day()
    {
        long a = await NewPlayerAsync("fp-help-a"), b = await NewPlayerAsync("fp-help-b");
        await server.CallGatewayAsync(801, new { friend_list = new[] { b } }, a);
        await server.CallGatewayAsync(111, new { friend_list = new[] { a } }, b);
        await server.CallGatewayAsync(60, new { }, a);

        for (int i = 0; i < 2; i++)
            Assert.Equal(0, (await server.CallGatewayAsync(71, new { friend_type = 1, stage_id = 3000001, stage_mode = 1, friend_user_no = b }, a))["errno"]!.GetValue<long>());
        JsonObject tab = await server.CallGatewayAsync(63, new { type = 1, page = 1, count = 30 }, b);
        Assert.Equal(1, tab["total_mail_count"]!.GetValue<long>());
        Assert.Equal(3, tab["mail_info"]![0]!["value1"]!.GetValue<long>());
    }

    [Fact]
    public async Task Bulk_friend_sends_refresh_cooldowns_without_paying_again_for_sent_or_invalid_targets()
    {
        long a = await NewPlayerAsync("bulk-points-a"), b = await NewPlayerAsync("bulk-points-b");
        long c = await NewPlayerAsync("bulk-points-c"), stranger = await NewPlayerAsync("bulk-points-stranger");
        await server.CallGatewayAsync(801, new { friend_list = new[] { b, c } }, a);
        await server.CallGatewayAsync(111, new { friend_list = new[] { a } }, b);
        await server.CallGatewayAsync(111, new { friend_list = new[] { a } }, c);
        await server.CallGatewayAsync(116, new { friend_list = new[] { b } }, a);

        JsonObject reply = await server.CallGatewayAsync(116, new { friend_list = new[] { b, c, c, stranger, a } }, a);
        Assert.Equal(2, reply["friend_info"]!.AsArray().Count);
        foreach (JsonNode? friend in reply["friend_info"]!.AsArray())
        {
            Assert.InRange(friend!["send_friendship_point_remain_time"]!.GetValue<long>(), 86390, 86400);
        }
        Assert.Equal(20, server.Scalar($"SELECT count FROM user_goods WHERE user_no={a} AND item_id=1900011"));
        Assert.Equal(2, reply["daily_quest_info"]!["quest_count"]!.GetValue<long>());
    }

    [Fact]
    public async Task The_ally_mission_board_fills_and_a_finished_mission_pays_out()
    {
        long user = await NewPlayerAsync("missions");
        server.Execute($"INSERT INTO fellow(user_no, fellow_id, level) VALUES({user}, 100001, 5)");
        long ally = server.Scalar($"SELECT MAX(rowid_alias) FROM fellow WHERE user_no={user}") + 7000;

        JsonObject board = await server.CallGatewayAsync(322, new { }, user);
        Assert.Equal(8, board["fellow_mission_infos"]!.AsArray().Count);
        long missionId = board["fellow_mission_infos"]![0]!["mission_id"]!.GetValue<long>();

        JsonObject started = await server.CallGatewayAsync(324, new { mission_id = missionId, fellow_no_list = new[] { ally }, soldier_id_list = Array.Empty<long>() }, user);
        Assert.Equal(0, Errno(started));
        Assert.Equal(2, started["fellow_mission_info"]!["status"]!.GetValue<long>());

        server.Execute($"UPDATE fellow_mission SET end_time=1, success_rate=100 WHERE user_no={user} AND mission_id={missionId}");
        JsonObject claimed = await server.CallGatewayAsync(325, new { mission_id_list = new[] { missionId }, use_cash = 0 }, user);
        Assert.Single(claimed["success_fellow_mission_infos"]!.AsArray());
        // The board refill may draw the same mission again, but only as a fresh open offer.
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow_mission WHERE user_no={user} AND mission_id={missionId} AND status<>0"));
    }

    [Fact]
    public async Task Mission_rewards_update_stamina_and_return_each_new_ally_item_once()
    {
        long user = await NewPlayerAsync("mission-stamina-gear");
        server.Execute($"INSERT INTO fellow(user_no,fellow_id,level) VALUES({user},100001,5)");
        long ally = server.Scalar($"SELECT MAX(rowid_alias) FROM fellow WHERE user_no={user}") + 7000;
        JsonObject board = await server.CallGatewayAsync(322, new { }, user);
        long mission = board["fellow_mission_infos"]![0]!["mission_id"]!.GetValue<long>();
        await server.CallGatewayAsync(324, new { mission_id = mission, fellow_no_list = new[] { ally }, soldier_id_list = Array.Empty<long>() }, user);
        server.Execute($"UPDATE fellow_mission SET end_time=1,success_rate=100,rewards='1900014:15;1201006:2' WHERE user_no={user} AND mission_id={mission}");
        JsonObject claim = await server.CallGatewayAsync(325, new { mission_id_list = new[] { mission }, use_cash = 0 }, user);
        Assert.Single(claim["success_fellow_mission_infos"]!.AsArray());
        JsonArray goods = claim["update_item_etc_info"]!.AsArray();
        Assert.Single(goods.Where(row => row!["item_id"]!.GetValue<long>() == 1900014));
        Assert.Equal(47, goods.Single(row => row!["item_id"]!.GetValue<long>() == 1900014)!["count"]!.GetValue<long>());
        JsonArray gear = claim["fellow_item_infos"]!.AsArray();
        Assert.Equal(2, gear.Count);
        Assert.All(gear, row => Assert.Equal(1201006, row!["fellow_item_id"]!.GetValue<long>()));
        Assert.Equal(2, gear.Select(row => row!["fellow_item_no"]!.GetValue<long>()).Distinct().Count());
        JsonObject repeat = await server.CallGatewayAsync(325, new { mission_id_list = new[] { mission }, use_cash = 0 }, user);
        Assert.Empty(repeat["fellow_item_infos"]!.AsArray());
        Assert.Equal(2, server.Scalar($"SELECT COUNT(*) FROM fellow_item WHERE user_no={user} AND fellow_item_id=1201006"));
        Assert.Equal(47, server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}"));
    }

    [Fact]
    public async Task Arena_lists_the_other_players_and_scores_a_battle()
    {
        long a = await NewPlayerAsync("arena-a"), b = await NewPlayerAsync("arena-b");

        JsonObject opponents = await server.CallGatewayAsync(408, new { refresh = 0, use_cash = 0 }, a);
        Assert.NotEmpty(opponents["pvp_user_info"]!.AsArray());
        Assert.DoesNotContain(opponents["pvp_user_info"]!.AsArray(), o => o!["user_no"]!.GetValue<long>() == a);

        JsonObject snapshot = await server.CallGatewayAsync(402, new { user_no = a, pvp_user_no = b, week_no = 1 }, a);
        Assert.Equal(b, snapshot["pvp_user_info"]!["user_info"]!["user_no"]!.GetValue<long>());
        Assert.Equal(1, snapshot["daily_quest_info"]!["quest_count"]!.GetValue<long>());

        JsonObject result = await server.CallGatewayAsync(403, new
        {
            user_no = a, pvp_user_no = b, pvp_result = 0, give_up = 0, pvp_player_hp = 500, pvp_player_max_hp = 1000, pvp_enemy_hp = 0, pvp_enemy_max_hp = 1000,
        }, a);
        Assert.Equal(14, result["user_pvp_ranking_info"]!["user_pvp_point"]!.GetValue<long>());
    }

    [Fact]
    public async Task Raid_shows_the_weekly_boss_and_keeps_the_best_score()
    {
        long user = await NewPlayerAsync("raid");

        JsonObject board = await server.CallGatewayAsync(501, new { user_no = user }, user);
        Assert.Single(board["ongoing_raid_info"]!.AsArray());

        JsonObject start = await server.CallGatewayAsync(503, new { raid_no = board["ongoing_raid_info"]![0]!["raid_no"]!.GetValue<long>() }, user);
        Assert.Equal(0, Errno(start));
        long stageId = board["ongoing_raid_info"]![0]!["stage_id"]!.GetValue<long>();
        JsonArray monsterRows = start[$"monster_info_{stageId}"]!.AsArray();
        Assert.Equal(13, monsterRows.Count);
        Assert.True(monsterRows[3]!["MaxHP"]!.GetValue<long>() > 0);

        await server.CallGatewayAsync(504, new { user_no = user, score = 500, max_damage = 900 }, user);
        await server.CallGatewayAsync(503, new { raid_no = board["ongoing_raid_info"]![0]!["raid_no"]!.GetValue<long>() }, user);
        JsonObject second = await server.CallGatewayAsync(504, new { user_no = user, score = 300 }, user);
        Assert.Equal(500, second["user_raid_info"]!["score"]!.GetValue<long>());
        Assert.Equal(2, second["user_raid_info"]!["count"]!.GetValue<long>());

        JsonObject ranking = await server.CallGatewayAsync(505, new { user_no = user, page = 1, count = 50 }, user);
        JsonArray ranked = ranking["raid_ranking_info"]!.AsArray();
        int position = ranked.ToList().FindIndex(row => row!["user_no"]!.GetValue<long>() == user);
        Assert.True(position >= 0);
        Assert.Equal(position + 1, ranked[position]!["rank"]!.GetValue<long>());
    }

    [Fact]
    public async Task Raid_remains_on_the_same_boss_when_a_score_exceeds_the_old_shared_health()
    {
        long user = await NewPlayerAsync("raid-killing-score");
        JsonObject board = await server.CallGatewayAsync(501, new { }, user);
        long raidNumber = board["ongoing_raid_info"]![0]!["raid_no"]!.GetValue<long>();
        await server.CallGatewayAsync(503, new { raid_no = raidNumber }, user);

        JsonObject result = await server.CallGatewayAsync(504, new { score = 1_000_000 }, user);

        Assert.Equal(raidNumber, result["user_raid_info"]!["raid_no"]!.GetValue<long>());
        Assert.Equal(1_000_000, result["user_raid_info"]!["score"]!.GetValue<long>());
        Assert.Equal(1, result["user_raid_info"]!["count"]!.GetValue<long>());
        JsonObject nextBoard = await server.CallGatewayAsync(501, new { }, user);
        Assert.Equal(raidNumber, nextBoard["ongoing_raid_info"]![0]!["raid_no"]!.GetValue<long>());
    }
}
