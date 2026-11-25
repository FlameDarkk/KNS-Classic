using System.Text.Json.Nodes;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public class ArenaLifecycleTests(ServerFixture server)
{
    private async Task<long> PlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        return user;
    }

    private Task<JsonObject> ResultAsync(long user, long opponent) => server.CallGatewayAsync(403,
        new { pvp_user_no = opponent, pvp_result = 0, give_up = 0, pvp_player_hp = 100, pvp_player_max_hp = 100,
            pvp_enemy_hp = 0, pvp_enemy_max_hp = 100 }, user);

    [Fact]
    public async Task Arena_needs_a_matching_fight_and_pays_and_spends_only_once()
    {
        long user = await PlayerAsync("arena-attempt"), opponent = await PlayerAsync("arena-attempt-opponent");
        long stranger = await PlayerAsync("arena-attempt-stranger");
        Assert.NotEqual(0, (await ResultAsync(user, opponent))["errno"]!.GetValue<long>());
        JsonObject started = await server.CallGatewayAsync(402, new { pvp_user_no = opponent }, user);
        Assert.Equal(9, started["update_item_etc_info"]!.AsArray().Single(row => row!["item_id"]!.GetValue<long>() == 1900017)!["count"]!.GetValue<long>());
        Assert.Equal(9, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900017"));
        Assert.NotEqual(0, (await ResultAsync(user, stranger))["errno"]!.GetValue<long>());
        JsonObject first = await ResultAsync(user, opponent);
        JsonObject repeated = await ResultAsync(user, opponent);
        Assert.Equal(14, first["user_pvp_ranking_info"]!["user_pvp_point"]!.GetValue<long>());
        Assert.Equal(14, repeated["user_pvp_ranking_info"]!["user_pvp_point"]!.GetValue<long>());
        Assert.Equal(1, server.Scalar($"SELECT battles FROM pvp_stat WHERE user_no={user}"));
        Assert.Equal(9, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900017"));
        Assert.Equal(9, first["update_item_etc_info"]!.AsArray().Single(row => row!["item_id"]!.GetValue<long>() == 1900017)!["count"]!.GetValue<long>());
    }

    [Fact]
    public async Task Give_up_and_last_spirit_entry_charge_once_and_do_not_block_settlement()
    {
        long user = await PlayerAsync("arena-last-spirit"), opponent = await PlayerAsync("arena-last-spirit-opponent");
        server.Execute($"INSERT OR REPLACE INTO user_goods(user_no,item_id,count) VALUES({user},1900017,1)");
        Assert.Equal(0, (await server.CallGatewayAsync(402, new { pvp_user_no = opponent }, user))["errno"]!.GetValue<long>());
        Assert.Equal(0, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900017"));
        Assert.Equal(0, (await ResultAsync(user, opponent))["errno"]!.GetValue<long>());
        await ResultAsync(user, opponent);
        Assert.Equal(0, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900017"));
        server.Execute($"UPDATE user_goods SET count=1 WHERE user_no={user} AND item_id=1900017");
        await server.CallGatewayAsync(402, new { pvp_user_no = opponent }, user);
        await server.CallGatewayAsync(403, new { pvp_user_no = opponent, give_up = 1 }, user);
        Assert.Equal(0, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900017"));
        // The give-up is recorded as a loss ("Giving up will be recorded as a loss").
        Assert.Equal(2, server.Scalar($"SELECT battles FROM pvp_stat WHERE user_no={user}"));
    }

    [Fact]
    public async Task Spirit_recovery_uses_persisted_half_hour_clock_and_preserves_overflow()
    {
        long user = await PlayerAsync("arena-recovery-clock");
        await server.CallGatewayAsync(408, new { }, user);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        server.Execute($"UPDATE user_goods SET count=6 WHERE user_no={user} AND item_id=1900017");
        server.Execute($"UPDATE pvp_spirit_clock SET last_gain={now - 3700} WHERE user_no={user}");
        JsonObject board = await server.CallGatewayAsync(408, new { }, user);
        JsonNode spirit = board["update_item_etc_info"]!.AsArray().Single(row => row!["item_id"]!.GetValue<long>() == 1900017)!;
        Assert.Equal(8, spirit["count"]!.GetValue<long>());
        Assert.InRange(spirit["user_last_stamina_gain_time"]!.GetValue<long>(), now - 101, now - 99);
        await server.CallGatewayAsync(408, new { }, user);
        Assert.Equal(8, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900017"));
        server.Execute($"UPDATE user_goods SET count=13 WHERE user_no={user} AND item_id=1900017");
        await server.CallGatewayAsync(408, new { }, user);
        Assert.Equal(13, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900017"));
    }

    [Fact]
    public async Task Refilling_spirits_preserves_reward_overflow_and_spends_gems_only_when_needed()
    {
        long user = await PlayerAsync("arena-refill");
        server.Execute($"UPDATE gameuser SET cash=100 WHERE user_no={user}");
        foreach (long balance in new long[] { 10, 13 })
        {
            server.Execute($"INSERT OR REPLACE INTO user_goods(user_no,item_id,count) VALUES({user},1900017,{balance})");
            Assert.NotEqual(0, (await server.CallGatewayAsync(407, new { }, user))["errno"]!.GetValue<long>());
            Assert.Equal(balance, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900017"));
            Assert.Equal(100, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        }
        server.Execute($"UPDATE user_goods SET count=0 WHERE user_no={user} AND item_id=1900017");
        JsonObject first = await server.CallGatewayAsync(407, new { }, user);
        Assert.Equal(0, first["errno"]!.GetValue<long>());
        Assert.Equal(10, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900017"));
        Assert.Equal(97, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(5, first["master_pvp_recovery_info"]!["PvpRecoveryValue"]!.GetValue<long>());

        server.Execute($"UPDATE user_goods SET count=0 WHERE user_no={user} AND item_id=1900017");
        JsonObject second = await server.CallGatewayAsync(407, new { }, user);
        Assert.Equal(0, second["errno"]!.GetValue<long>());
        Assert.Equal(92, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(10, second["master_pvp_recovery_info"]!["PvpRecoveryValue"]!.GetValue<long>());

        server.Execute($"UPDATE user_goods SET count=0 WHERE user_no={user} AND item_id=1900017");
        JsonObject third = await server.CallGatewayAsync(407, new { }, user);
        Assert.Equal(0, third["errno"]!.GetValue<long>());
        Assert.Equal(82, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(10, third["master_pvp_recovery_info"]!["PvpRecoveryValue"]!.GetValue<long>());
    }

    [Fact]
    public async Task Own_profile_card_type_101_identifies_owner_inside_hero_and_keeps_current_equipment()
    {
        long user = await PlayerAsync("arena-own-profile");
        server.Execute($"INSERT INTO user_hero(user_no,hero_no,hero_pcid) VALUES({user},2,2); UPDATE gameuser SET hero_pcid=2 WHERE user_no={user}");
        long[] bag;
        using (IServiceScope scope = server.CreateScope())
        {
            FellowService fellows = scope.ServiceProvider.GetRequiredService<FellowService>();
            FellowRepository slots = scope.ServiceProvider.GetRequiredService<FellowRepository>();
            bag = Enumerable.Range(0, 5).Select(_ => fellows.Grant(user, 100001)).ToArray();
            slots.SetSlot(user, 3, 0, bag[4]);
            slots.SetSlot(user, 3, 2, bag[2]);
            scope.ServiceProvider.GetRequiredService<KnS.Data.DataSession>().Commit();
        }
        JsonObject before = await server.CallGatewayAsync(60, new { }, user);
        JsonObject detail = await server.CallGatewayAsync(64, new { friend_user_no = user, type = 101 }, user);
        Assert.Equal(0, detail["errno"]!.GetValue<long>());
        Assert.Equal(101, detail["type"]!.GetValue<long>());
        Assert.Equal(user, detail["hero_info"]!["user_no"]!.GetValue<long>());
        JsonNode activeHero = before["hero_info"]!.AsArray().Single(hero => hero!["hero_pcid"]!.GetValue<long>() == 2)!;
        Assert.Equal(2, detail["hero_info"]!["hero_pcid"]!.GetValue<long>());
        Assert.Equal(activeHero["hero_item_infos"]!.ToJsonString(), detail["hero_info"]!["hero_item_infos"]!.ToJsonString());
        Assert.Equal([bag[4], 0, bag[2]], detail["fellow_info"]!.AsArray().Select(fellow => fellow!["fellow_no"]!.GetValue<long>()));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM pvp_play WHERE user_no={user}"));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM daily_quest_progress WHERE user_no={user} AND quest_id=5300006"));
    }

    [Fact]
    public async Task Arena_battle_uses_the_preview_party_and_its_gear()
    {
        long user = await PlayerAsync("arena-lineup-viewer"), opponent = await PlayerAsync("arena-lineup-opponent");
        long[] bag;
        long equipped;
        using (IServiceScope scope = server.CreateScope())
        {
            var fellows = scope.ServiceProvider.GetRequiredService<FellowService>();
            var slots = scope.ServiceProvider.GetRequiredService<FellowRepository>();
            var items = scope.ServiceProvider.GetRequiredService<FellowItemRepository>();
            long[] allyIds = [100211, 100221, 100251, 100399, 100403, 100404];
            bag = allyIds.Select(id => fellows.Grant(opponent, id)).ToArray();
            slots.SetSlot(opponent, 3, 0, bag[5]);
            slots.SetSlot(opponent, 3, 2, bag[3]);
            slots.SetSlot(opponent, 3, 3, bag[4]);
            equipped = items.Add(opponent, 1200001);
            items.SetEquipped(equipped, bag[5], 1);
            items.SetEquipped(items.Add(opponent, 1200001), bag[0], 1);
            scope.ServiceProvider.GetRequiredService<KnS.Data.DataSession>().Commit();
        }

        JsonObject preview = await server.CallGatewayAsync(64, new { friend_user_no = opponent, type = 100 }, user);
        Assert.Equal(0, preview["errno"]!.GetValue<long>());
        Assert.Equal([bag[5], 0, bag[3]], preview["fellow_info"]!.AsArray().Select(fellow => fellow!["fellow_no"]!.GetValue<long>()));
        Assert.Equal(equipped, Assert.Single(preview["fellow_item_infos"]!.AsArray())!["fellow_item_no"]!.GetValue<long>());

        JsonObject start = await server.CallGatewayAsync(402, new { pvp_user_no = opponent }, user);
        Assert.Equal(0, start["errno"]!.GetValue<long>());
        JsonNode roster = start["pvp_user_info"]!;
        Assert.Equal([bag[5], 0, bag[3]], roster["fellow_info"]!.AsArray().Select(fellow => fellow!["fellow_no"]!.GetValue<long>()));
        Assert.True(JsonNode.DeepEquals(preview["fellow_info"], roster["fellow_info"]));
        Assert.True(JsonNode.DeepEquals(preview["fellow_item_infos"], roster["fellow_item_infos"]));
    }

    [Fact]
    public async Task Own_card_is_a_view_with_allies_and_spends_nothing_and_battles_without_spirits_are_refused()
    {
        long user = await PlayerAsync("arena-empty"), opponent = await PlayerAsync("arena-empty-opponent");
        JsonObject self = await server.CallGatewayAsync(402, new { pvp_user_no = user }, user);
        Assert.Equal(0, self["errno"]!.GetValue<long>());
        Assert.Equal(user, self["pvp_user_info"]!["user_info"]!["user_no"]!.GetValue<long>());
        Assert.NotNull(self["pvp_user_info"]!["fellow_info"]);
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM pvp_play WHERE user_no={user}"));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM daily_quest_progress WHERE user_no={user} AND quest_id=5300006"));
        server.Execute($"INSERT OR REPLACE INTO user_goods(user_no,item_id,count) VALUES({user},1900017,0)");
        Assert.NotEqual(0, (await server.CallGatewayAsync(402, new { pvp_user_no = opponent }, user))["errno"]!.GetValue<long>());
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM daily_quest_progress WHERE user_no={user} AND quest_id=5300006"));
    }

    [Fact]
    public async Task Arena_season_returns_populated_ranking_and_mission_rewards()
    {
        long user = await PlayerAsync("arena-season-rewards");
        JsonObject season = await server.CallGatewayAsync(405, new { }, user);
        Assert.Equal(0, season["errno"]?.GetValue<long>() ?? 0);
        JsonArray ranking = season["pvp_ranking_reward_info"]!.AsArray();
        JsonArray mission = season["pvp_mission_reward_info"]!.AsArray();
        Assert.Equal(20, ranking.Count);
        Assert.Equal(28, mission.Count);

        // Grade 1 ranking reward (Season 1 features 6★ Battle Queen Caroline 100499; Grade 4 = 5★ 100498; Grade 9 = normal Ally Ticket 1900012).
        Assert.Equal(1, ranking[0]!["Grade"]!.GetValue<long>());
        Assert.Equal(100499, ranking[0]!["Reward1_Item"]!.GetValue<long>());
        Assert.Equal(1, ranking[0]!["Reward1_Value"]!.GetValue<long>());
        Assert.Equal(500, ranking[0]!["Reward2_Value"]!.GetValue<long>());
        Assert.Equal(9993501, ranking[0]!["Text_Index"]!.GetValue<long>());
        Assert.Equal(100498, ranking[3]!["Reward1_Item"]!.GetValue<long>());
        Assert.Equal(1900012, ranking[8]!["Reward1_Item"]!.GetValue<long>());

        // Grade 28 mission reward
        Assert.Equal(28, mission[27]!["Grade"]!.GetValue<long>());
        Assert.Equal(1400, mission[27]!["Score"]!.GetValue<long>());
        Assert.Equal(1900019, mission[27]!["Reward_Item"]!.GetValue<long>());
        Assert.Equal(1, mission[27]!["Reward_Value"]!.GetValue<long>());
    }

    [Fact]
    public async Task Ranking_assigns_second_place_two_and_uses_the_same_rank_in_records()
    {
        long first = await PlayerAsync("arena-rank-first"), second = await PlayerAsync("arena-rank-second"), third = await PlayerAsync("arena-rank-third");
        foreach ((long user, long points) in new[] { (first, 100000L), (second, 99999L), (third, 99998L) })
            server.Execute($"INSERT OR REPLACE INTO pvp_stat(user_no,points) VALUES({user},{points})");
        JsonObject reply = await server.CallGatewayAsync(406, new { page = 1, count = 3 }, second);
        JsonArray ranking = reply["pvp_ranking_info"]!.AsArray();
        Assert.Equal(second, ranking[1]!["user_no"]!.GetValue<long>());
        Assert.Equal(2, ranking[1]!["rank"]!.GetValue<long>());
        JsonObject board = await server.CallGatewayAsync(408, new { }, second);
        Assert.Equal(2, board["user_pvp_ranking_info"]!["rank"]!.GetValue<long>());
    }

    [Fact]
    public async Task Free_refresh_starts_half_hour_cooldown_resets_streak_and_gem_refresh_spends_gems_and_keeps_streak()
    {
        long user = await PlayerAsync("arena-refresh-user");
        for (int i = 0; i < 8; i++)
        {
            await PlayerAsync($"arena-refresh-rival-{i}");
        }

        server.Execute($"INSERT OR REPLACE INTO pvp_stat(user_no,points,battles,wins,streak) VALUES({user},28,2,2,2)");
        server.Execute($"UPDATE gameuser SET cash=50 WHERE user_no={user}");

        JsonObject initial = await server.CallGatewayAsync(408, new { refresh = 0, use_cash = 0 }, user);
        Assert.Equal(0, initial["errno"]!.GetValue<long>());
        Assert.Equal(0, initial["remain_refresh_time"]!.GetValue<long>());
        Assert.Equal(2, initial["user_pvp_ranking_info"]!["consecutive_win_count"]!.GetValue<long>());
        long[] initialOpponents = initial["pvp_user_info"]!.AsArray().Select(row => row!["user_no"]!.GetValue<long>()).ToArray();

        // Free refresh: starts 1800s (30-min) cooldown, resets streak to 0, rotates opponents, spends 0 gems.
        JsonObject freeRefresh = await server.CallGatewayAsync(408, new { refresh = 1, use_cash = 0 }, user);
        Assert.Equal(0, freeRefresh["errno"]!.GetValue<long>());
        Assert.InRange(freeRefresh["remain_refresh_time"]!.GetValue<long>(), 1795, 1800);
        Assert.Equal(0, freeRefresh["user_pvp_ranking_info"]!["consecutive_win_count"]!.GetValue<long>());
        Assert.Equal(50, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        long[] afterFree = freeRefresh["pvp_user_info"]!.AsArray().Select(row => row!["user_no"]!.GetValue<long>()).ToArray();
        Assert.NotEqual(initialOpponents, afterFree);

        // Second free refresh while on cooldown is refused with errno 22007 and remaining cooldown time.
        JsonObject cooldownBlocked = await server.CallGatewayAsync(408, new { refresh = 1, use_cash = 0 }, user);
        Assert.Equal(22007, cooldownBlocked["errno"]!.GetValue<long>());
        Assert.InRange(cooldownBlocked["remain_refresh_time"]!.GetValue<long>(), 1790, 1800);

        // Give the player a 3-win streak while still on cooldown, then use Gem Refresh (5 gems):
        // deducts 5 gems, preserves the 3-win streak, rotates opponents, and refreshes the 1800s timer.
        server.Execute($"UPDATE pvp_stat SET streak=3 WHERE user_no={user}");
        JsonObject gemRefresh = await server.CallGatewayAsync(408, new { refresh = 1, use_cash = 1 }, user);
        Assert.Equal(0, gemRefresh["errno"]!.GetValue<long>());
        Assert.Equal(3, gemRefresh["user_pvp_ranking_info"]!["consecutive_win_count"]!.GetValue<long>());
        Assert.InRange(gemRefresh["remain_refresh_time"]!.GetValue<long>(), 1795, 1800);
        Assert.Equal(45, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(45, gemRefresh["update_item_etc_info"]!.AsArray().Single(row => row!["item_id"]!.GetValue<long>() == 1900009)!["count"]!.GetValue<long>());
        long[] afterGem = gemRefresh["pvp_user_info"]!.AsArray().Select(row => row!["user_no"]!.GetValue<long>()).ToArray();
        Assert.NotEqual(afterFree, afterGem);

        // Gem refresh without enough gems is refused and does not change streak or seed.
        server.Execute($"UPDATE gameuser SET cash=4 WHERE user_no={user}");
        JsonObject brokeGem = await server.CallGatewayAsync(408, new { refresh = 1, use_cash = 1 }, user);
        Assert.NotEqual(0, brokeGem["errno"]!.GetValue<long>());
        Assert.Equal(4, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(3, server.Scalar($"SELECT streak FROM pvp_stat WHERE user_no={user}"));
    }
    [Fact]
    public async Task Beating_all_five_listed_opponents_gives_five_new_ones_and_keeps_the_streak()
    {
        long user = await PlayerAsync("arena-sweep-user");
        for (int i = 0; i < 20; i++)
        {
            long rival = await PlayerAsync($"arena-sweep-rival-{i}");
            server.Execute($"INSERT OR REPLACE INTO pvp_stat(user_no,points) VALUES({rival},{50000 + i})");
        }
        server.Execute($"INSERT OR REPLACE INTO pvp_stat(user_no,points) VALUES({user},50005)");
        long[] Ids(JsonObject board) => board["pvp_user_info"]!.AsArray().Select(row => row!["user_no"]!.GetValue<long>()).ToArray();

        long[] first = Ids(await server.CallGatewayAsync(408, new { refresh = 0, use_cash = 0 }, user));
        Assert.Equal(5, first.Length);
        long[] scores = first.Select(no => server.Scalar($"SELECT points FROM pvp_stat WHERE user_no={no}")).ToArray();
        // Nearest in score: drawn from the 15 closest, here every rival lies within 50000..50019.
        Assert.All(scores, score => Assert.InRange(score, 50000, 50019));
        Assert.Equal(scores.OrderByDescending(x => x), scores);

        for (int i = 0; i < first.Length; i++)
        {
            await server.CallGatewayAsync(402, new { pvp_user_no = first[i] }, user);
            Assert.Equal(1, (await ResultAsync(user, first[i]))["pvp_result"]!.GetValue<long>());
            if (i < first.Length - 1)
            {
                // Between battles the list stays the same even though the player's score moved.
                Assert.Equal(first, Ids(await server.CallGatewayAsync(408, new { refresh = 0, use_cash = 0 }, user)));
            }
        }

        // The result scene re-requests 408 with refresh 0 once all are beaten.
        JsonObject next = await server.CallGatewayAsync(408, new { refresh = 0, use_cash = 0 }, user);
        long[] second = Ids(next);
        Assert.Equal(5, second.Length);
        Assert.Empty(second.Intersect(first));
        Assert.All(next["pvp_user_info"]!.AsArray(), row => Assert.Equal(0, row!["pvp_result"]!.GetValue<long>()));
        Assert.Equal(5, next["user_pvp_ranking_info"]!["consecutive_win_count"]!.GetValue<long>());
        Assert.Equal(0, next["remain_refresh_time"]!.GetValue<long>());
        Assert.Equal(second, Ids(await server.CallGatewayAsync(408, new { refresh = 0, use_cash = 0 }, user)));

        // A refresh also avoids the previous five when enough players exist.
        server.Execute($"UPDATE gameuser SET cash=50 WHERE user_no={user}");
        long[] third = Ids(await server.CallGatewayAsync(408, new { refresh = 1, use_cash = 1 }, user));
        Assert.Empty(third.Intersect(second));
    }
}
