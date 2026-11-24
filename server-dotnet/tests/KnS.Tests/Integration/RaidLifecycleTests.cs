using System.Text.Json.Nodes;
using KnS.Data.Repositories;
using KnS.Game.Friends;
using KnS.Game.Protocols.Raid;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public class RaidLifecycleTests(ServerFixture server)
{
    private async Task<(long User, long Raid)> PlayerAsync(string name)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = name }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        JsonObject board = await server.CallGatewayAsync(501, new { }, user);
        return (user, board["ongoing_raid_info"]![0]!["raid_no"]!.GetValue<long>());
    }

    [Fact]
    public void Browser_raid_challenges_are_loaded_as_master_data()
    {
        Assert.Equal(196, server.Scalar("SELECT COUNT(*) FROM master_raid_challenge"));
        Assert.Equal(7, server.Scalar("SELECT COUNT(DISTINCT raid_id) FROM master_raid_challenge"));
        Assert.Equal(20_000_000, server.Scalar("SELECT threshold FROM master_raid_challenge WHERE challenge_id=5300001 AND raid_id=3100011"));
        Assert.Equal(40, server.Scalar("SELECT reward_count FROM master_raid_challenge WHERE challenge_id=5300001 AND reward_item_id=1900009"));
    }

    [Fact]
    public async Task Raid_results_require_a_started_fight_and_duplicate_results_count_once()
    {
        var (user, raid) = await PlayerAsync("raid-replay");
        JsonObject unsolicited = await server.CallGatewayAsync(504, new { score = 123 }, user);
        Assert.NotEqual(0, unsolicited["errno"]!.GetValue<long>());
        await server.CallGatewayAsync(503, new { raid_no = raid }, user);
        JsonObject first = await server.CallGatewayAsync(504, new { score = 500 }, user);
        JsonObject duplicate = await server.CallGatewayAsync(504, new { score = 9999 }, user);
        Assert.Equal(1, first["user_raid_info"]!["count"]!.GetValue<long>());
        Assert.Equal(1, duplicate["user_raid_info"]!["count"]!.GetValue<long>());
        Assert.Equal(500, duplicate["user_raid_info"]!["current_score"]!.GetValue<long>());
        Assert.Equal(500, duplicate["user_raid_info"]!["score"]!.GetValue<long>());
    }

    [Fact]
    public async Task Results_after_a_week_boundary_keep_the_persisted_start_week()
    {
        var (user, currentRaid) = await PlayerAsync("raid-week-boundary");
        await server.CallGatewayAsync(503, new { raid_no = currentRaid }, user);
        // Simulate an already-started fight from the previous week, without changing the global test clock.
        long startedRaid = currentRaid - 1000;
        server.Execute($"UPDATE raid_play SET raid_no={startedRaid} WHERE user_no={user}");
        JsonObject result = await server.CallGatewayAsync(504, new { score = 777 }, user);
        Assert.Equal(startedRaid, result["user_raid_info"]!["raid_no"]!.GetValue<long>());
        Assert.Equal(777, server.Scalar($"SELECT score FROM raid_score WHERE user_no={user} AND raid_no={startedRaid}"));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM raid_score WHERE user_no={user} AND raid_no={currentRaid}"));
    }

    [Fact]
    public async Task A_stale_raid_start_is_refused_before_incrementing_daily_progress()
    {
        var (user, raid) = await PlayerAsync("raid-stale");
        JsonObject reply = await server.CallGatewayAsync(503, new { raid_no = raid - 1000 }, user);
        Assert.NotEqual(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM daily_quest_progress WHERE user_no={user} AND quest_id=5300003"));
    }

    [Fact]
    public async Task Ten_starts_exhaust_today_even_without_results_and_the_eleventh_is_refused()
    {
        var (user, raid) = await PlayerAsync("raid-daily-limit");
        for (int count = 1; count <= 10; count++)
        {
            JsonObject start = await server.CallGatewayAsync(503, new { raid_no = raid }, user);
            Assert.Equal(0, start["errno"]!.GetValue<long>());
            Assert.Equal(count, start["user_raid_info"]!["count"]!.GetValue<long>());
        }
        JsonObject refused = await server.CallGatewayAsync(503, new { raid_no = raid }, user);
        Assert.NotEqual(0, refused["errno"]!.GetValue<long>());
        Assert.Equal(10, server.Scalar($"SELECT SUM(quest_count) FROM daily_quest_progress WHERE user_no={user} AND quest_id=5300003"));
        JsonObject result = await server.CallGatewayAsync(504, new { score = 4321 }, user);
        Assert.Equal(10, result["user_raid_info"]!["count"]!.GetValue<long>());
        Assert.Equal(1, server.Scalar($"SELECT plays FROM raid_score WHERE user_no={user} AND raid_no={raid}"));
    }

    [Fact]
    public async Task Utc_midnight_restores_entries_without_erasing_scores_or_recharging_late_results()
    {
        var (user, number) = await PlayerAsync("raid-daily-midnight");
        using var scope = server.CreateScope();
        var clock = new RaidTestClock(new DateTimeOffset(2026, 11, 15, 23, 59, 59, TimeSpan.Zero));
        var repository = scope.ServiceProvider.GetRequiredService<RaidRepository>();
        var raid = new RaidService(repository, scope.ServiceProvider.GetRequiredService<FriendService>(),
            scope.ServiceProvider.GetRequiredService<PlayerRepository>(), clock);
        repository.RecordPlay(number, user, 9000);
        for (int i = 0; i < 10; i++) Assert.True(raid.BeginPlay(user, number));
        Assert.False(raid.BeginPlay(user, number));
        Assert.Equal(10, raid.GetUserInfo(user, raidNumber: number).Count);
        clock.Now = clock.Now.AddSeconds(1);
        UserRaidInfo late = Assert.IsType<UserRaidInfo>(raid.CompletePlay(user, 100));
        Assert.Equal(0, late.Count);
        Assert.Equal(9000, late.Score);
        Assert.True(late.Rank > 0);
        Assert.Equal(2, repository.Get(number, user).Plays);
        Assert.Equal(0, raid.CompletePlay(user, 99999)!.Count);
        Assert.True(raid.BeginPlay(user, number));
        Assert.Equal(1, raid.GetUserInfo(user, raidNumber: number).Count);
        Assert.True(raid.BeginPlay(user, number + 1));
        Assert.Equal(2, raid.GetUserInfo(user, raidNumber: number + 1).Count);
    }

    [Fact]
    public async Task Legacy_current_day_starts_seed_the_daily_counter_once_even_after_quest_claim()
    {
        var (user, raid) = await PlayerAsync("raid-daily-legacy");
        long day = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400;
        server.Execute($"INSERT INTO daily_quest_progress(user_no,day_no,quest_id,quest_count,complete) VALUES({user},{day},5300003,7,1)");
        JsonObject board = await server.CallGatewayAsync(505, new { }, user);
        Assert.Equal(7, board["user_raid_ranking_info"]!["count"]!.GetValue<long>());
        JsonObject start = await server.CallGatewayAsync(503, new { raid_no = raid }, user);
        Assert.Equal(8, start["user_raid_info"]!["count"]!.GetValue<long>());
        Assert.Equal(7, server.Scalar($"SELECT quest_count FROM daily_quest_progress WHERE user_no={user} AND day_no={day} AND quest_id=5300003"));
        board = await server.CallGatewayAsync(505, new { }, user);
        Assert.Equal(8, board["user_raid_ranking_info"]!["count"]!.GetValue<long>());
    }

    [Fact]
    public async Task Play_again_buff_increments_one_percent_per_play_up_to_fifteen_and_resets_on_boss_rotation()
    {
        var (user, raid) = await PlayerAsync("raid-play-again-buff");
        JsonObject status0 = await server.CallGatewayAsync(501, new { }, user);
        Assert.Equal(0, status0["raid_buff_count"]!.GetValue<long>());

        // Play 1: +0% -> score 1000, post-play buff = 1
        Assert.Equal(0, (await server.CallGatewayAsync(503, new { raid_no = raid }, user))["errno"]!.GetValue<long>());
        JsonObject res1 = await server.CallGatewayAsync(504, new { score = 1000 }, user);
        Assert.Equal(1000, res1["user_raid_info"]!["current_score"]!.GetValue<long>());
        Assert.Equal(1, res1["raid_buff_count"]!.GetValue<long>());
        // Duplicate 504 stays idempotent.
        JsonObject dup1 = await server.CallGatewayAsync(504, new { score = 1000 }, user);
        Assert.Equal(1000, dup1["user_raid_info"]!["current_score"]!.GetValue<long>());
        Assert.Equal(1, dup1["raid_buff_count"]!.GetValue<long>());

        // Play 2: +1% -> score 1010, post-play buff = 2
        Assert.Equal(0, (await server.CallGatewayAsync(503, new { raid_no = raid }, user))["errno"]!.GetValue<long>());
        JsonObject res2 = await server.CallGatewayAsync(504, new { score = 1000 }, user);
        Assert.Equal(1010, res2["user_raid_info"]!["current_score"]!.GetValue<long>());
        Assert.Equal(2, res2["raid_buff_count"]!.GetValue<long>());
        Assert.Equal(2, (await server.CallGatewayAsync(501, new { }, user))["raid_buff_count"]!.GetValue<long>());

        // Simulate 15 completed plays -> +15% MAX, stays capped at 15.
        server.Execute($"UPDATE raid_score SET plays=15 WHERE raid_no={raid} AND user_no={user}");
        Assert.Equal(15, (await server.CallGatewayAsync(501, new { }, user))["raid_buff_count"]!.GetValue<long>());
        Assert.Equal(0, (await server.CallGatewayAsync(503, new { raid_no = raid }, user))["errno"]!.GetValue<long>());
        JsonObject resMax = await server.CallGatewayAsync(504, new { score = 1000 }, user);
        Assert.Equal(1150, resMax["user_raid_info"]!["current_score"]!.GetValue<long>());
        Assert.Equal(15, resMax["raid_buff_count"]!.GetValue<long>());

        // New raid week resets raid_buff_count to 0.
        using var scope = server.CreateScope();
        var raidService = scope.ServiceProvider.GetRequiredService<RaidService>();
        Assert.Equal(0, raidService.GetBuffCount(user, raid + 1000));
    }

    private sealed class RaidTestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
