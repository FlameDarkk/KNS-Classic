using System.Text.Json.Nodes;
using KnS.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public class AchievementProgressTests(ServerFixture server)
{
    private async Task<long> PlayerAsync(string name)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = name }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        return user;
    }

    private static long Count(JsonObject reply, long type, string field = "update_achievement_info") =>
        reply[field]!.AsArray().FirstOrDefault(row => row!["type"]!.GetValue<long>() == type)?["count"]?.GetValue<long>() ?? 0;

    [Fact]
    public async Task Acquisitions_count_once_and_survive_sales_spending_and_reload()
    {
        long user = await PlayerAsync("achievement-acquisitions");
        JsonObject before = await server.CallGatewayAsync(121, new { }, user);
        long equipment = Count(before, 40006, "achievement_infos");
        long materials = Count(before, 40007, "achievement_infos");
        long gold = Count(before, 40009, "achievement_infos");
        using (var scope = server.CreateScope())
        {
            var items = scope.ServiceProvider.GetRequiredService<ItemRepository>();
            long offer = items.Add(user, 200001, false, default);
            Assert.NotNull(items.Claim(user, offer));
            Assert.Null(items.Claim(user, offer));
            items.Add(user, 1700001, true, default);
            items.Add(user, 1700001, true, default);
            // Unclaimed offers and shop stock must not count as collected equipment.
            items.Add(user, 200001, false, default);
            var players = scope.ServiceProvider.GetRequiredService<PlayerRepository>();
            players.AddCurrency(user, gold: 500);
            players.AddCurrency(user, gold: -700);
        }
        JsonObject updated = await server.CallGatewayAsync(121, new { }, user);
        Assert.Equal(equipment + 1, Count(updated, 40006, "achievement_infos"));
        Assert.Equal(materials + 2, Count(updated, 40007, "achievement_infos"));
        Assert.Equal(gold + 500, Count(updated, 40009, "achievement_infos"));
        server.Execute($"DELETE FROM item WHERE user_no={user}");
        JsonObject reload = await server.CallGatewayAsync(121, new { }, user);
        Assert.Equal(equipment + 1, Count(reload, 40006, "achievement_infos"));
        Assert.Equal(materials + 2, Count(reload, 40007, "achievement_infos"));
        Assert.Equal(gold + 500, Count(reload, 40009, "achievement_infos"));
    }

    [Fact]
    public async Task Buying_second_character_updates_possession_without_double_counting_repeat_buy()
    {
        long user = await PlayerAsync("achievement-heroes");
        server.Execute($"UPDATE gameuser SET cash=100000 WHERE user_no={user}");
        long hero = server.Scalar($"SELECT hero_pcid FROM master_hero WHERE hero_pcid NOT IN(SELECT hero_pcid FROM user_hero WHERE user_no={user}) LIMIT 1");
        JsonObject bought = await server.CallGatewayAsync(201, new { hero_pcid = hero }, user);
        Assert.Equal(0, bought["errno"]!.GetValue<long>());
        Assert.Equal(2, Count(bought, 40057));
        Assert.Equal(2, Count(await server.CallGatewayAsync(201, new { hero_pcid = hero }, user), 40057));
        Assert.Equal(2, Count(await server.CallGatewayAsync(121, new { }, user), 40057, "achievement_infos"));
    }

    [Fact]
    public async Task Raid_counts_completed_plays_across_reloads_but_not_replayed_or_unsolicited_results()
    {
        long user = await PlayerAsync("achievement-raids");
        Assert.NotEqual(0, (await server.CallGatewayAsync(504, new { score = 50 }, user))["errno"]!.GetValue<long>());
        long raid = (await server.CallGatewayAsync(501, new { }, user))["ongoing_raid_info"]![0]!["raid_no"]!.GetValue<long>();
        for (long i = 1; i <= 3; i++)
        {
            await server.CallGatewayAsync(503, new { raid_no = raid }, user);
            JsonObject result = await server.CallGatewayAsync(504, new { score = 50 }, user);
            Assert.Equal(i, Count(result, 40031));
            Assert.Equal(i, Count(await server.CallGatewayAsync(504, new { score = 100 }, user), 40031));
        }
        Assert.Equal(3, Count(await server.CallGatewayAsync(121, new { }, user), 40031, "achievement_infos"));
    }

    [Fact]
    public async Task Friendship_counts_successful_recipients_without_client_report_or_cooldown_duplicates()
    {
        long user = await PlayerAsync("achievement-fp"), friend = await PlayerAsync("achievement-fp-target");
        await server.CallGatewayAsync(801, new { friend_list = new[] { friend } }, user);
        await server.CallGatewayAsync(111, new { friend_list = new[] { user } }, friend);
        JsonObject sent = await server.CallGatewayAsync(116, new { friend_list = new[] { friend, friend, user } }, user);
        Assert.Equal(1, Count(sent, 40067));
        Assert.Equal(1, Count(await server.CallGatewayAsync(116, new { friend_list = new[] { friend } }, user), 40067));
        server.Execute($"UPDATE friend_link SET last_sent=0 WHERE user_no={user} AND friend_no={friend}");
        JsonObject again = await server.CallGatewayAsync(116, new { friend_list = new[] { friend }, achievement_list = new[] { new { type = 40067, count = 1 } } }, user);
        Assert.Equal(2, Count(again, 40067));
    }

    [Fact]
    public async Task All_25_areas_need_every_stage_in_matching_difficulty_and_restore_existing_clears()
    {
        long user = await PlayerAsync("achievement-areas");
        server.Execute($"INSERT INTO stage_clear(user_no,stage_id,stage_mode,clear_count) SELECT {user},stage_id,1,1 FROM master_stage_area WHERE area_id IN(SELECT sub_type FROM master_achievement WHERE type BETWEEN 1001 AND 1025)");
        // One missing stage must leave this area incomplete.
        server.Execute($"DELETE FROM stage_clear WHERE user_no={user} AND stage_id=3000001");
        JsonObject partial = await server.CallGatewayAsync(121, new { }, user);
        Assert.Equal(0, Count(partial, 41001, "achievement_infos"));
        Assert.Equal(1, Count(partial, 41025, "achievement_infos"));
        Assert.Equal(0, Count(partial, 42025, "achievement_infos"));
        server.Execute($"INSERT INTO stage_clear(user_no,stage_id,stage_mode,clear_count) VALUES({user},3000001,1,1)");
        server.Execute($"INSERT INTO stage_clear(user_no,stage_id,stage_mode,clear_count) SELECT {user},stage_id,2,1 FROM master_stage_area WHERE area_id IN(SELECT sub_type FROM master_achievement WHERE type BETWEEN 2001 AND 2025)");
        JsonObject all = await server.CallGatewayAsync(121, new { }, user);
        for (long i = 1; i <= 25; i++)
        {
            Assert.Equal(1, Count(all, 41000 + i, "achievement_infos"));
            Assert.Equal(1, Count(all, 42000 + i, "achievement_infos"));
        }
    }

    [Fact]
    public async Task Arena_best_streak_survives_loss_reload_and_duplicate_result()
    {
        long user = await PlayerAsync("achievement-streak"), opponent = await PlayerAsync("achievement-streak-target");
        for (long i = 1; i <= 3; i++)
        {
            await server.CallGatewayAsync(402, new { pvp_user_no = opponent }, user);
            var parameters = new { pvp_user_no = opponent, pvp_result = 0, give_up = 0, pvp_player_hp = 100, pvp_player_max_hp = 100, pvp_enemy_hp = 0, pvp_enemy_max_hp = 100 };
            Assert.Equal(i, Count(await server.CallGatewayAsync(403, parameters, user), 40082));
            Assert.Equal(i, Count(await server.CallGatewayAsync(403, parameters, user), 40082));
            opponent = await PlayerAsync("achievement-streak-target-" + i);
        }
        await server.CallGatewayAsync(402, new { pvp_user_no = opponent }, user);
        Assert.Equal(3, Count(await server.CallGatewayAsync(403, new { pvp_user_no = opponent, give_up = 1 }, user), 40082));
        Assert.Equal(0, server.Scalar($"SELECT streak FROM pvp_stat WHERE user_no={user}"));
        Assert.Equal(3, Count(await server.CallGatewayAsync(121, new { }, user), 40082, "achievement_infos"));
    }

    [Fact]
    public async Task Client_event_deltas_accumulate_while_combo_keeps_maximum_and_refusals_do_not_add()
    {
        long user = await PlayerAsync("achievement-deltas");
        var counters = new[] { new { type = 43002, count = 2 }, new { type = 40051, count = 44 } };
        await server.CallGatewayAsync(116, new { friend_list = Array.Empty<long>(), achievement_list = counters }, user);
        JsonObject second = await server.CallGatewayAsync(116, new { friend_list = Array.Empty<long>(), achievement_list = counters }, user);
        Assert.Equal(4, Count(second, 43002));
        Assert.Equal(44, Count(second, 40051));
        await server.CallGatewayAsync(123, new { achievement_id = 999999, achievement_list = counters }, user);
        JsonObject reload = await server.CallGatewayAsync(121, new { }, user);
        Assert.Equal(4, Count(reload, 43002, "achievement_infos"));
        var huge = new[] { new { type = 43005, count = long.MaxValue } };
        await server.CallGatewayAsync(116, new { friend_list = Array.Empty<long>(), achievement_list = huge }, user);
        JsonObject saturated = await server.CallGatewayAsync(116, new { friend_list = Array.Empty<long>(), achievement_list = huge }, user);
        Assert.Equal(long.MaxValue, Count(saturated, 43005));
    }
}

