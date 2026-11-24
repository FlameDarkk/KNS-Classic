using System.Text.Json.Nodes;
using KnS.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public class MissionWireLifecycleTests(ServerFixture server)
{
    private static readonly int[] MissionIds = [6210089];
    private static readonly long[] TierMissionIds = [6210006L];

    [Theory]
    [InlineData(100, "success_fellow_mission_infos", 777)]
    [InlineData(0, "fail_fellow_mission_infos", 0)]
    public async Task Open_start_timer_finished_result_and_replay_use_native_states(long chance, string resultKey, long payout)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = $"mission-wire-{chance}" }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        server.Execute($"INSERT INTO fellow(user_no,fellow_id,level) VALUES({user},100001,5)");
        using var scope = server.CreateScope();
        long ally = scope.ServiceProvider.GetRequiredService<FellowRepository>().GetOwned(user)[0].FellowNumber;
        server.Execute($"DELETE FROM fellow_mission WHERE user_no={user}");
        server.Execute($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,end_time,success_rate,rewards) VALUES({user},6210089,0,'','',0,60,'1900008:777')");
        JsonObject board = await server.CallGatewayAsync(322, new { }, user);
        JsonNode open = board["fellow_mission_infos"]!.AsArray().Single(row => row!["mission_id"]!.GetValue<long>() == 6210089)!;
        Assert.Equal(1, open["status"]!.GetValue<long>());
        JsonObject started = await server.CallGatewayAsync(324, new { mission_id = 6210089, fellow_no_list = new[] { ally } }, user);
        Assert.Equal(0, started["errno"]!.GetValue<long>());
        Assert.Equal(2, started["fellow_mission_info"]!["status"]!.GetValue<long>());
        Assert.Equal(ally, started["fellow_mission_info"]!["fellow_no1"]!.GetValue<long>());
        Assert.InRange(started["fellow_mission_info"]!["mission_end_remain_time"]!.GetValue<long>(), 3590, 3600);
        JsonObject early = await server.CallGatewayAsync(325, new { mission_id_list = MissionIds, use_cash = 0 }, user);
        Assert.Empty(early[resultKey]!.AsArray());
        server.Execute($"UPDATE fellow_mission SET end_time=1,success_rate={chance} WHERE user_no={user} AND mission_id=6210089");
        JsonObject finished = await server.CallGatewayAsync(322, new { }, user);
        JsonNode done = finished["fellow_mission_infos"]!.AsArray().Single(row => row!["mission_id"]!.GetValue<long>() == 6210089)!;
        Assert.Equal(3, done["status"]!.GetValue<long>());
        Assert.Equal(0, done["mission_end_remain_time"]!.GetValue<long>());
        long gold = server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");
        JsonObject result = await server.CallGatewayAsync(325, new { mission_id_list = MissionIds, use_cash = 0 }, user);
        // The Mission Report sums the rewards of every listed mission, so a failed one lists none.
        Assert.Equal(payout, Assert.Single(result[resultKey]!.AsArray())!["reward_value1"]!.GetValue<long>());
        Assert.Equal(gold + payout, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        JsonObject repeated = await server.CallGatewayAsync(325, new { mission_id_list = MissionIds, use_cash = 0 }, user);
        Assert.Empty(repeated["success_fellow_mission_infos"]!.AsArray());
        Assert.Empty(repeated["fail_fellow_mission_infos"]!.AsArray());
        Assert.Equal(gold + payout, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task Mission_reward_items_count_toward_the_active_story_gather_objective()
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = "mission-story-tears" }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        foreach (long done in new long[] { 7000001, 7000002, 7000003, 7000004, 7000005, 7000006 })
        {
            server.Execute($"INSERT INTO story_completion(user_no, story_id) VALUES({user}, {done})");
        }

        // 7000007: gather 80 World Tree Branches and 20 Mermaid's Tears.
        server.Execute($"DELETE FROM fellow_mission WHERE user_no={user}");
        server.Execute($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,end_time,success_rate,rewards) VALUES({user},6210089,1,'','',1,100,'1901002:7;1901007:3')");
        JsonObject result = await server.CallGatewayAsync(325, new { mission_id_list = MissionIds, use_cash = 0 }, user);
        Assert.Single(result["success_fellow_mission_infos"]!.AsArray());

        JsonNode story = (await server.CallGatewayAsync(151, new { }, user))["story_infos"]!.AsArray()
            .Single(row => row!["story_id"]!.GetValue<long>() == 7000007)!;
        Assert.Equal(3, story["quest1_count"]!.GetValue<long>());
        Assert.Equal(7, story["quest2_count"]!.GetValue<long>());
    }

    [Fact]
    public async Task Mission_claim_awards_native_exp_and_board_filters_by_tier_not_country()
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = "mission-tier-exp" }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        server.Execute($"INSERT INTO fellow(user_no,fellow_id,level,exp) VALUES({user},100001,1,0)");
        using var scope = server.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<FellowMissionRepository>();
        long ally = scope.ServiceProvider.GetRequiredService<FellowRepository>().GetOwned(user)[0].FellowNumber;

        // 6210006 has col4 (duration) = 0, col41 (text_index / native +0xa4 EXP) = 89.
        FellowMissionRule rule = repo.FindRule(6210006)!;
        Assert.Equal(89, rule.Experience);
        Assert.Equal(0, rule.TierScore);

        server.Execute($"DELETE FROM fellow_mission WHERE user_no={user}");
        server.Execute($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,end_time,success_rate,rewards) VALUES({user},6210006,1,'{ally}','',1,100,'1900008:10')");
        JsonObject claim = await server.CallGatewayAsync(325, new { mission_id_list = TierMissionIds, use_cash = 0 }, user);
        Assert.Single(claim["success_fellow_mission_infos"]!.AsArray());
        // The 89 EXP may level the ally up: count the levels gained plus the remainder.
        Assert.Equal(89, server.Scalar(
            $"SELECT f.exp + COALESCE((SELECT SUM(l.exp_to_next) FROM master_fellow_level l WHERE l.grade=m.grade AND l.level<f.level), 0)"
            + $" FROM fellow f JOIN master_fellow m ON m.fellow_id=f.fellow_id WHERE f.user_no={user}"));

        // At player level 45, all board missions come from TierScore [9000..20000] regardless of country (level 1..12).
        server.Execute($"UPDATE gameuser SET level=45 WHERE user_no={user}");
        server.Execute($"DELETE FROM fellow_mission WHERE user_no={user}");
        JsonObject highBoard = await server.CallGatewayAsync(322, new { }, user);
        JsonArray list = highBoard["fellow_mission_infos"]!.AsArray();
        Assert.Equal(8, list.Count);
        foreach (JsonNode? item in list)
        {
            FellowMissionRule? drawn = repo.FindRule(item!["mission_id"]!.GetValue<long>());
            Assert.NotNull(drawn);
            Assert.InRange(drawn.TierScore, 9000, 20000);
        }
    }
}
