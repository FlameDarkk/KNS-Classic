using System.Text.Json.Nodes;
using KnS.Game.Protocols.Arena;
using KnS.Game.Rules;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public class ClassicNativeAlignmentTests(ServerFixture server)
{
    private async Task<long> PlayerAsync(string suffix) =>
        (await server.CallGatewayAsync(51, new { hub_did = $"align-{suffix}-{Guid.NewGuid():N}" }))["user_no"]!.GetValue<long>();

    [Fact]
    public async Task Stage_start_sends_native_gold_pile_count_and_max_per_stage()
    {
        long user = await PlayerAsync("gold-piles");
        JsonObject s1 = await server.CallGatewayAsync(71, new { stage_id = 3000001, stage_mode = 1 }, user);
        Assert.Equal(0, s1["errno"]!.GetValue<long>());
        Assert.Equal(10, s1["stage_info"]!["drop_count"]!.GetValue<long>());
        Assert.Equal(20, s1["stage_info"]!["drop_gold_max"]!.GetValue<long>());

        JsonObject boss = await server.CallGatewayAsync(71, new { stage_id = 3000011, stage_mode = 1 }, user);
        Assert.Equal(0, boss["errno"]!.GetValue<long>());
        Assert.Equal(1, boss["stage_info"]!["drop_count"]!.GetValue<long>());
        Assert.Equal(260, boss["stage_info"]!["drop_gold_max"]!.GetValue<long>());
    }

    [Fact]
    public async Task Login_sends_native_initial_pvp_recovery_cost_of_3_gems()
    {
        long user = await PlayerAsync("pvp-login-recovery");
        JsonObject load = await server.CallGatewayAsync(60, new { }, user);
        Assert.Equal(0, load["errno"]!.GetValue<long>());
        Assert.Equal(3, load["master_pvp_recovery_info"]!["PvpRecoveryValue"]!.GetValue<long>());
    }

    [Fact]
    public async Task Arena_consecutive_wins_add_streak_bonus_and_populate_buff_remain_time()
    {
        long user = await PlayerAsync("streak-winner");
        long opponent = await PlayerAsync("streak-opp");
        await server.CallGatewayAsync(408, new { }, user);

        // 1st win (KO = 14 pts, bonus = 0)
        Assert.Equal(0, (await server.CallGatewayAsync(402, new { pvp_user_no = opponent }, user))["errno"]!.GetValue<long>());
        JsonObject r1 = await server.CallGatewayAsync(403, new { pvp_user_no = opponent, pvp_player_hp = 100, pvp_player_max_hp = 100, pvp_enemy_hp = 0, pvp_enemy_max_hp = 100 }, user);
        Assert.Equal(14, r1["user_pvp_ranking_info"]!["user_pvp_point"]!.GetValue<long>());
        Assert.Equal(1, r1["user_pvp_ranking_info"]!["consecutive_win_count"]!.GetValue<long>());
        Assert.True(r1["user_pvp_ranking_info"]!["buff_remain_time"]!.GetValue<long>() > 0);

        // 2nd consecutive win (KO = 14 pts + 1 bonus = +15 -> 29 total)
        Assert.Equal(0, (await server.CallGatewayAsync(402, new { pvp_user_no = opponent }, user))["errno"]!.GetValue<long>());
        JsonObject r2 = await server.CallGatewayAsync(403, new { pvp_user_no = opponent, pvp_player_hp = 100, pvp_player_max_hp = 100, pvp_enemy_hp = 0, pvp_enemy_max_hp = 100 }, user);
        Assert.Equal(29, r2["user_pvp_ranking_info"]!["user_pvp_point"]!.GetValue<long>());
        Assert.Equal(2, r2["user_pvp_ranking_info"]!["consecutive_win_count"]!.GetValue<long>());

        // Consecutive bonus caps at +10 (Pvp_MaxConsecutiveScore)
        Assert.Equal(0, ArenaRules.ConsecutiveBonus(1));
        Assert.Equal(1, ArenaRules.ConsecutiveBonus(2));
        Assert.Equal(10, ArenaRules.ConsecutiveBonus(11));
        Assert.Equal(10, ArenaRules.ConsecutiveBonus(25));
    }

    [Fact]
    public void Event_dungeon_armor_uses_native_Event_Armor_option_table()
    {
        Assert.Equal("Event_Armor", ItemOptionRules.OptionCountSource(11, 300000));
        Assert.Equal("Event_Armor", ItemOptionRules.OptionCountSource(15, 300000));
        Assert.Equal("Nomal", ItemOptionRules.OptionCountSource(11, 200000));
        Assert.Equal("Hard", ItemOptionRules.OptionCountSource(2, 300000));
        Assert.Equal("ItemShop", ItemOptionRules.OptionCountSource(3, 300000));
    }

    [Fact]
    public async Task Friend_helper_uses_8h_cooldown_and_daily_delete_cap_is_5()
    {
        long user = await PlayerAsync("friend-cap-main");
        long[] others = new long[6];
        for (int i = 0; i < 6; i++)
        {
            others[i] = await PlayerAsync($"friend-cap-{i}");
            await server.CallGatewayAsync(801, new { friend_list = new[] { others[i] } }, user);
            await server.CallGatewayAsync(111, new { friend_list = new[] { user } }, others[i]);
        }

        // Bring others[0] as helper in stage 3000001 -> HelpRemainTime should be ~8 hours (<= 28800s)
        await server.CallGatewayAsync(71, new { stage_id = 3000001, stage_mode = 1, friend_user_no = others[0] }, user);
        JsonObject list = await server.CallGatewayAsync(803, new { page = 1, count = 30 }, user);
        JsonNode helperRow = list["friend_info"]!.AsArray().Single(r => r!["user_no"]!.GetValue<long>() == others[0])!;
        long helpRemain = helperRow["help_remain_time"]!.GetValue<long>();
        Assert.InRange(helpRemain, 7 * 3600, 8 * 3600);

        // Delete 5 friends succeeds; 6th delete on same day is refused (DailyFriendDeleteMaxCount = 5)
        for (int i = 0; i < 5; i++)
        {
            JsonObject del = await server.CallGatewayAsync(112, new { friend_user_no = others[i] }, user);
            Assert.Equal(0, del["errno"]!.GetValue<long>());
            Assert.Equal(i + 1, del["friend_del_count"]!.GetValue<long>());
        }
        JsonObject sixth = await server.CallGatewayAsync(112, new { friend_user_no = others[5] }, user);
        Assert.NotEqual(0, sixth["errno"]!.GetValue<long>());
    }

    [Fact]
    public async Task Protocol_332_expands_ally_equipment_bag_using_native_gold_and_gem_schedule()
    {
        long user = await PlayerAsync("ally-gear-bag");
        server.Execute($"UPDATE gameuser SET gold=50000, cash=50 WHERE user_no={user}");

        JsonObject lvl1 = await server.CallGatewayAsync(332, new { expansion_level = 1 }, user);
        Assert.Equal(0, lvl1["errno"]!.GetValue<long>());
        Assert.Equal(3, lvl1["user_info"]!["user_fellow_item_inventory_size_cash"]!.GetValue<long>());
        Assert.Equal(40000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));

        JsonObject lvl2 = await server.CallGatewayAsync(332, new { expansion_level = 2 }, user);
        Assert.Equal(0, lvl2["errno"]!.GetValue<long>());
        Assert.Equal(6, lvl2["user_info"]!["user_fellow_item_inventory_size_cash"]!.GetValue<long>());
        Assert.Equal(45, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));

        JsonObject load = await server.CallGatewayAsync(60, new { }, user);
        Assert.Equal(6, load["user_info"]!["user_fellow_item_inventory_size_cash"]!.GetValue<long>());
    }

    [Fact]
    public async Task Protocol_124_returns_update_achievement_info_with_level_and_stored_progress()
    {
        long user = await PlayerAsync("achieve-124");
        server.Execute($"UPDATE gameuser SET level=12 WHERE user_no={user}");
        JsonObject res = await server.CallGatewayAsync(124, new { }, user);
        Assert.Equal(0, res["errno"]!.GetValue<long>());
        JsonArray updates = res["update_achievement_info"]!.AsArray();
        Assert.Contains(updates, row => row!["type"]!.GetValue<long>() == 40000 && row["count"]!.GetValue<long>() == 12);
    }
}
