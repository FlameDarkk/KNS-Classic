using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

/// <summary>The story chain past the first three stories: stage-clear, item and ally objectives and chapter changes.</summary>
[Collection(SharedServer.Name)]
public class StoryChainTests(ServerFixture server)
{
    private static readonly long[] NoPickups = [];

    private async Task<long> PlayerAtAsync(string device, params long[] completed)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        foreach (long story in completed)
        {
            server.Execute($"INSERT INTO story_completion(user_no, story_id) VALUES({user}, {story})");
        }

        return user;
    }

    private Task<JsonObject> ClearAsync(long user, long stage, int result = 1)
    {
        // Isolate story progression while satisfying the one-result-per-attempt contract.
        server.Execute($"INSERT OR REPLACE INTO stage_start(user_no,stage_id) VALUES({user},{stage})");
        return server.CallGatewayAsync(72, new { stage_no = stage, stage_mode = 1, stage_result = result, reward_gold = 0, reward_items = NoPickups, event_items = NoPickups }, user);
    }

    [Fact]
    public async Task Defeating_Strong_Bighand_completes_Big_Hands_Big_Problems_and_opens_the_next_story()
    {
        long user = await PlayerAtAsync("story-bighand", 7000001, 7000002, 7000003);
        Assert.NotEqual(0, (await server.CallGatewayAsync(152, new { story_id = 7000004 }, user))["errno"]!.GetValue<long>());
        Assert.Equal(0, (await ClearAsync(user, 3000031, result: 0))["story_info"]!["quest1_count"]!.GetValue<long>());
        Assert.Equal(0, (await ClearAsync(user, 3000024))["story_info"]!["quest1_count"]!.GetValue<long>());

        JsonObject cleared = await ClearAsync(user, 3000031);
        Assert.Equal(7000004, cleared["story_info"]!["story_id"]!.GetValue<long>());
        Assert.Equal(1, cleared["story_info"]!["quest1_count"]!.GetValue<long>());

        long gold = server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");
        long points = server.Scalar($"SELECT COALESCE(SUM(count),0) FROM user_goods WHERE user_no={user} AND item_id=1900016");
        JsonObject done = await server.CallGatewayAsync(152, new { story_id = 7000004 }, user);
        Assert.Equal(0, done["errno"]!.GetValue<long>());
        Assert.Equal(1, done["complete_story_info"]!["complete"]!.GetValue<long>());
        Assert.Equal(7000005, done["story_info"]!["story_id"]!.GetValue<long>());
        Assert.Equal(gold + 500, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.Equal(points + 50, server.Scalar($"SELECT COALESCE(SUM(count),0) FROM user_goods WHERE user_no={user} AND item_id=1900016"));
        Assert.NotEqual(0, (await server.CallGatewayAsync(152, new { story_id = 7000004 }, user))["errno"]!.GetValue<long>());

        JsonArray list = (await server.CallGatewayAsync(151, new { }, user))["story_infos"]!.AsArray();
        Assert.Equal([7000001, 7000002, 7000003, 7000004, 7000005], list.Select(row => row!["story_id"]!.GetValue<long>()));
    }

    [Fact]
    public async Task Ally_objective_and_chapter_change_follow_the_previous_story_link()
    {
        long user = await PlayerAtAsync("story-chapter", 7000001, 7000002, 7000003, 7000004);
        Assert.NotEqual(0, (await server.CallGatewayAsync(152, new { story_id = 7000005 }, user))["errno"]!.GetValue<long>());
        server.Execute($"INSERT INTO fellow_book(user_no, fellow_id) VALUES({user}, 100351)");
        Assert.Equal(0, (await server.CallGatewayAsync(152, new { story_id = 7000005 }, user))["errno"]!.GetValue<long>());

        // 7000007 has two objectives (80 and 20 branches) and gives an item; the next story is 7000101 (chapter II).
        server.Execute($"INSERT INTO story_completion(user_no, story_id) VALUES({user}, 7000006)");
        server.Execute($"INSERT INTO story_progress(user_no, story_id, quest_count, quest2_count) VALUES({user}, 7000007, 80, 19)");
        Assert.NotEqual(0, (await server.CallGatewayAsync(152, new { story_id = 7000007 }, user))["errno"]!.GetValue<long>());
        server.Execute($"UPDATE story_progress SET quest2_count=20 WHERE user_no={user} AND story_id=7000007");
        JsonObject done = await server.CallGatewayAsync(152, new { story_id = 7000007 }, user);
        Assert.Equal(0, done["errno"]!.GetValue<long>());
        Assert.Equal(7000101, done["story_info"]!["story_id"]!.GetValue<long>());
        Assert.Contains(done["item_info"]!.AsArray(), row => row!["item_id"]!.GetValue<long>() == 900401);
    }
}
