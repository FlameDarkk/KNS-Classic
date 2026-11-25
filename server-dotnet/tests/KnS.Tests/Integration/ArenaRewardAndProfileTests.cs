using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

/// <summary>Arena give-up, score mission payouts, profile ranks, Last Played and the ally mission refresh price (2026-11-11).</summary>
[Collection(SharedServer.Name)]
public class ArenaRewardAndProfileTests(ServerFixture server)
{
    private async Task<long> PlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        return user;
    }

    private Task<JsonObject> FightAsync(long user, long opponent, int giveUp, long playerHp, long enemyHp) => server.CallGatewayAsync(403,
        new { pvp_user_no = opponent, pvp_result = 0, give_up = giveUp, pvp_player_hp = playerHp, pvp_player_max_hp = 100,
            pvp_enemy_hp = enemyHp, pvp_enemy_max_hp = 100 }, user);

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 2)]
    [InlineData(10, 10, 4)]
    [InlineData(11, 200, 5)]
    [InlineData(100, 100, 8)]
    [InlineData(101, 10_000, 10)]   // 1.01% rounds up into 2~5%
    [InlineData(101, 101, 20)]      // last of 101: 100%
    [InlineData(150, 300, 15)]      // 50% -> 41~50%
    public void Ranking_reward_tier_follows_the_reward_tab(long rank, long total, long grade) =>
        Assert.Equal(grade, KnS.Game.Protocols.Arena.ArenaRules.RankingRewardFor(rank, total)!.Grade);

    [Fact]
    public void Ranking_reward_needs_a_valid_rank() =>
        Assert.Null(KnS.Game.Protocols.Arena.ArenaRules.RankingRewardFor(5, 4));

    private static readonly long[] ExpectedSixStarCycle = [100499, 100502, 100512, 100520, 100522, 105015, 105017, 100499];
    private static readonly long[] ExpectedFiveStarCycle = [100498, 100501, 100511, 100519, 100521, 105014, 105016, 100498];

    [Fact]
    public void Arena_limited_allies_cycle_across_the_seven_altar_pairs_and_assign_six_and_five_star_tiers()
    {
        for (int season = 1; season <= 8; season++)
        {
            var pair = KnS.Game.Protocols.Arena.ArenaRules.FeaturedAllyFor(season);
            Assert.Equal(ExpectedSixStarCycle[season - 1], pair.SixStarId);
            Assert.Equal(ExpectedFiveStarCycle[season - 1], pair.FiveStarId);
            var table = KnS.Game.Protocols.Arena.ArenaRules.RankingRewardsFor(pair);
            Assert.Equal(20, table.Count);
            Assert.All(table.Where(r => r.Grade is >= 1 and <= 3), r => Assert.Equal(pair.SixStarId, r.Reward1Item));
            Assert.All(table.Where(r => r.Grade is >= 4 and <= 8), r => Assert.Equal(pair.FiveStarId, r.Reward1Item));
            Assert.All(table.Where(r => r.Grade >= 9), r => Assert.Equal(1900012, r.Reward1Item));
            Assert.Equal(500, table[0].Reward2Value);
            Assert.Equal(90, table[7].Reward2Value);
            Assert.Equal(70, table[8].Reward2Value);
        }

        // Pin overrides auto-rotation; unknown override falls back to auto.
        Assert.Equal(100512, KnS.Game.Protocols.Arena.ArenaRules.FeaturedAllyFor(1, 100512).SixStarId);
        Assert.Equal(100499, KnS.Game.Protocols.Arena.ArenaRules.FeaturedAllyFor(1, 999999).SixStarId);
    }

    [Fact]
    public async Task Give_up_is_a_zero_point_loss_and_only_wins_mark_the_opponent()
    {
        long user = await PlayerAsync("arena-giveup-loss"), quit = await PlayerAsync("arena-giveup-quit"), beaten = await PlayerAsync("arena-giveup-beaten");
        await server.CallGatewayAsync(402, new { pvp_user_no = quit }, user);
        // Ahead on hit points when giving up: still a loss.
        JsonObject gaveUp = await FightAsync(user, quit, 1, 90, 10);
        Assert.Equal(0, gaveUp["errno"]!.GetValue<long>());
        Assert.Equal(7, gaveUp["pvp_result"]!.GetValue<long>());
        Assert.Equal(0, gaveUp["user_pvp_ranking_info"]!["user_pvp_point"]!.GetValue<long>());
        Assert.Equal(1, gaveUp["user_pvp_ranking_info"]!["total_pvp_count"]!.GetValue<long>());
        Assert.Equal(0, gaveUp["user_pvp_ranking_info"]!["total_win_count"]!.GetValue<long>());
        Assert.Equal(7, server.Scalar($"SELECT result FROM pvp_opponent_result WHERE user_no={user} AND opponent_no={quit}"));

        await server.CallGatewayAsync(402, new { pvp_user_no = beaten }, user);
        Assert.Equal(1, (await FightAsync(user, beaten, 0, 100, 0))["pvp_result"]!.GetValue<long>());
        Assert.Equal(1, server.Scalar($"SELECT result FROM pvp_opponent_result WHERE user_no={user} AND opponent_no={beaten}"));

        // A free refresh starts a new list: the old marks go.
        server.Execute($"UPDATE pvp_stat SET refresh_until=0 WHERE user_no={user}");
        await server.CallGatewayAsync(408, new { refresh = 1, use_cash = 0 }, user);
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM pvp_opponent_result WHERE user_no={user}"));
    }

    [Fact]
    public async Task Reaching_a_score_mission_mails_its_reward_once_and_flags_the_result()
    {
        long user = await PlayerAsync("arena-mission-pay"), opponent = await PlayerAsync("arena-mission-pay-opponent");
        server.Execute($"INSERT OR REPLACE INTO pvp_stat(user_no,points) VALUES({user},45)");
        await server.CallGatewayAsync(402, new { pvp_user_no = opponent }, user);
        JsonObject won = await FightAsync(user, opponent, 0, 100, 0);
        Assert.Equal(59, won["user_pvp_ranking_info"]!["user_pvp_point"]!.GetValue<long>());
        Assert.Equal(1, won["is_reward"]!.GetValue<long>());
        Assert.Equal([1L], won["grade"]!.AsArray().Select(g => g!.GetValue<long>()));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM reward_mail WHERE user_no={user} AND item_id1=1900014 AND value1=20"));
        // Message Box title: native text table 14, 9800022 "PvP Mission Reward".
        Assert.Equal(9800022, server.Scalar($"SELECT text_code FROM reward_mail WHERE user_no={user} AND item_id1=1900014"));

        await server.CallGatewayAsync(402, new { pvp_user_no = opponent }, user);
        JsonObject again = await FightAsync(user, opponent, 0, 100, 0);
        Assert.Equal(0, again["is_reward"]!.GetValue<long>());
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM reward_mail WHERE user_no={user} AND item_id1=1900014"));
    }

    [Fact]
    public async Task Opening_the_arena_backfills_missions_already_reached()
    {
        long user = await PlayerAsync("arena-mission-backfill");
        server.Execute($"INSERT OR REPLACE INTO pvp_stat(user_no,points) VALUES({user},120)");
        await server.CallGatewayAsync(408, new { }, user);
        await server.CallGatewayAsync(408, new { }, user);
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM reward_mail WHERE user_no={user} AND item_id1=1900014 AND value1=20"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM reward_mail WHERE user_no={user} AND item_id1=1902002 AND value1=5"));
        Assert.Equal(2, server.Scalar($"SELECT COUNT(*) FROM pvp_mission_paid WHERE user_no={user}"));
    }

    [Fact]
    public async Task Profile_card_shows_arena_rank_after_a_battle_and_no_raid_rank_without_plays()
    {
        long user = await PlayerAsync("profile-rank"), opponent = await PlayerAsync("profile-rank-opponent");
        JsonObject before = await server.CallGatewayAsync(64, new { friend_user_no = user, type = 4 }, opponent);
        Assert.Equal(0, before["pvp_rank"]!.GetValue<long>());
        await server.CallGatewayAsync(402, new { pvp_user_no = opponent }, user);
        await FightAsync(user, opponent, 0, 100, 0);
        JsonObject after = await server.CallGatewayAsync(64, new { friend_user_no = user, type = 4 }, opponent);
        Assert.True(after["pvp_rank"]!.GetValue<long>() > 0);
        Assert.Equal(0, after["raid_rank"]!.GetValue<long>());
    }

    [Fact]
    public async Task Last_played_never_passes_the_viewers_clock_base()
    {
        long viewer = await PlayerAsync("last-played-viewer"), other = await PlayerAsync("last-played-other");
        long viewerSync = server.Scalar($"SELECT last_sync FROM gameuser WHERE user_no={viewer}");
        Assert.True(viewerSync > 0);
        // The other player was active after the viewer's login; a time older than the base must stay as it is.
        server.Execute($"UPDATE gameuser SET last_sync={viewerSync - 3600} WHERE user_no={viewer}");
        server.Execute($"UPDATE gameuser SET last_seen={viewerSync - 7200} WHERE user_no={other}");
        JsonObject ranking = await server.CallGatewayAsync(406, new { page = 1, count = 100 }, viewer);
        long Seen(long number) => ranking["pvp_ranking_info"]!.AsArray().Single(r => r!["user_no"]!.GetValue<long>() == number)!["user_last_login_time"]!.GetValue<long>();
        Assert.Equal(viewerSync - 7200, Seen(other));
        Assert.Equal(viewerSync - 3600, Seen(viewer));
    }

    [Fact]
    public async Task Ally_mission_refresh_escalates_by_five_gems_up_to_one_hundred_and_resets_daily()
    {
        long user = await PlayerAsync("mission-refresh-price");
        server.Execute($"UPDATE gameuser SET cash=20 WHERE user_no={user}");
        JsonObject board = await server.CallGatewayAsync(322, new { }, user);
        Assert.Equal(5, board["next_mission_refresh_info"]!["MissionRefreshValue"]!.GetValue<long>());

        JsonObject first = await server.CallGatewayAsync(323, new { }, user);
        Assert.Equal(0, first["errno"]!.GetValue<long>());
        Assert.Equal(15, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(10, first["next_mission_refresh_info"]!["MissionRefreshValue"]!.GetValue<long>());

        JsonObject second = await server.CallGatewayAsync(323, new { }, user);
        Assert.Equal(0, second["errno"]!.GetValue<long>());
        Assert.Equal(5, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(15, second["next_mission_refresh_info"]!["MissionRefreshValue"]!.GetValue<long>());

        // Not enough gems for the 3rd refresh (costs 15, only 5 remaining).
        Assert.NotEqual(0, (await server.CallGatewayAsync(323, new { }, user))["errno"]!.GetValue<long>());
        Assert.Equal(5, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));

        // Next UTC day resets the refresh price back to 5 Gems.
        server.Execute($"UPDATE fellow_mission_state SET refresh_day=refresh_day-1 WHERE user_no={user}");
        JsonObject nextDay = await server.CallGatewayAsync(322, new { }, user);
        Assert.Equal(5, nextDay["next_mission_refresh_info"]!["MissionRefreshValue"]!.GetValue<long>());
    }

    [Fact]
    public async Task Zero_score_players_do_not_receive_arena_ranking_rewards()
    {
        long scorer = await PlayerAsync("arena-nonzero-scorer"), gaveUp = await PlayerAsync("arena-zero-giveup"), idle = await PlayerAsync("arena-zero-idle");
        await server.CallGatewayAsync(402, new { pvp_user_no = idle }, scorer);
        await FightAsync(scorer, idle, 0, 100, 0);
        await server.CallGatewayAsync(402, new { pvp_user_no = idle }, gaveUp);
        await FightAsync(gaveUp, idle, 1, 90, 10);

        Assert.Equal(1, server.Scalar($"SELECT battles FROM pvp_stat WHERE user_no={gaveUp}"));
        Assert.Equal(0, server.Scalar($"SELECT points FROM pvp_stat WHERE user_no={gaveUp}"));

        using (var scope = server.CreateScope())
            scope.ServiceProvider.GetRequiredService<KnS.Game.Protocols.Arena.ArenaService>().NextSeason();

        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM reward_mail WHERE text_code=9800021 AND user_no={scorer}"));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM reward_mail WHERE text_code=9800021 AND user_no={gaveUp}"));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM reward_mail WHERE text_code=9800021 AND user_no={idle}"));
        Assert.Empty((await server.CallGatewayAsync(404, new { }, gaveUp))["reward_pvp_info"]!.AsObject());
        Assert.Empty((await server.CallGatewayAsync(404, new { }, idle))["reward_pvp_info"]!.AsObject());
    }
}
