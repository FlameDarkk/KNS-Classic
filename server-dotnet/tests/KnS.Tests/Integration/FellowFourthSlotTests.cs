using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

/// <summary>
/// The rebuild's fourth dungeon slot (<c>party_slots: 4</c> on 60, 302 and 72). Without the flag every reply is what the
/// original client expects: its three setups sit back to back in memory, so a position 3 must never reach it.
/// </summary>
[Collection(SharedServer.Name)]
public class FellowFourthSlotTests(ServerFixture server)
{
    private const long ElevenDrawPackage = 9_710_005;

    private async Task<(long User, long[] Fellows)> NewPlayerWithFellowsAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        JsonObject drawn = await server.CallGatewayAsync(92, new
        {
            package_id = ElevenDrawPackage,
            use_ticket = 0,
            group_num = 1,
            achievement_list = Array.Empty<long>(),
        }, user);
        long[] numbers = drawn["fellow_info"]!.AsArray().Select(f => f!["fellow_no"]!.GetValue<long>()).ToArray();
        Assert.True(numbers.Length >= 5);
        return (user, numbers);
    }

    private static object Setup(long type, params long[] fellows) => new
    {
        type,
        fellows = fellows.Select((number, position) => new { fellow_no = number, fellow_position = position }).ToArray(),
    };

    private static object SetupFlagged(long type, params long[] fellows) => new
    {
        type,
        party_slots = 4,
        fellows = fellows.Select((number, position) => new { fellow_no = number, fellow_position = position }).ToArray(),
    };

    private static long[] Positions(JsonNode? fellows) =>
        fellows!.AsArray().Select(f => f!["fellow_no"]!.GetValue<long>()).ToArray();

    private static long[] Party(JsonObject loaded, long type) =>
        loaded["fellow_active_info"]!.AsArray()
            .Where(slot => slot!["type"]!.GetValue<long>() == type)
            .OrderBy(slot => slot!["fellow_position"]!.GetValue<long>())
            .Select(slot => slot!["fellow_no"]!.GetValue<long>())
            .ToArray();

    [Fact]
    public async Task A_flagged_302_keeps_four_dungeon_slots_and_the_other_setups_three()
    {
        var (user, f) = await NewPlayerWithFellowsAsync("fourth-302");

        JsonObject dungeon = await server.CallGatewayAsync(302, SetupFlagged(3, f[0], f[1], f[2], f[3]), user);
        JsonObject arena = await server.CallGatewayAsync(302, SetupFlagged(1, f[4], f[3], f[2], f[1]), user);

        Assert.Equal([f[0], f[1], f[2], f[3]], Positions(dungeon["fellows"]));
        Assert.Equal([f[4], f[3], f[2]], Positions(arena["fellows"]));
    }

    [Fact]
    public async Task An_unflagged_302_answers_three_positions_and_ignores_position_3()
    {
        var (user, f) = await NewPlayerWithFellowsAsync("fourth-302-original");

        JsonObject reply = await server.CallGatewayAsync(302, Setup(3, f[0], f[1], f[2], f[3]), user);
        JsonObject loaded = await server.CallGatewayAsync(60, new { user_no = user }, user);

        Assert.Equal([f[0], f[1], f[2]], Positions(reply["fellows"]));
        Assert.Equal([f[0], f[1], f[2]], Party(loaded, 3));
    }

    [Fact]
    public async Task An_original_302_leaves_the_fourth_slot_alone_and_60_hides_it_from_the_original()
    {
        var (user, f) = await NewPlayerWithFellowsAsync("fourth-60");
        await server.CallGatewayAsync(302, SetupFlagged(3, f[0], f[1], f[2], f[3]), user);

        JsonObject original = await server.CallGatewayAsync(60, new { user_no = user }, user);
        JsonObject rebuild = await server.CallGatewayAsync(60, new { user_no = user, party_slots = 4 }, user);
        JsonObject cleared = await server.CallGatewayAsync(302, Setup(3, 0, 0, 0), user);
        JsonObject afterOriginal = await server.CallGatewayAsync(60, new { user_no = user, party_slots = 4 }, user);

        Assert.Equal([f[0], f[1], f[2]], Party(original, 3));
        Assert.All(original["fellow_active_info"]!.AsArray(), slot => Assert.InRange(slot!["fellow_position"]!.GetValue<long>(), 0, 2));
        Assert.Equal([f[0], f[1], f[2], f[3]], Party(rebuild, 3));
        Assert.Equal([0L, 0, 0], Positions(cleared["fellows"]));
        Assert.Equal([f[3]], Party(afterOriginal, 3));
    }

    [Fact]
    public async Task An_original_302_moving_the_fourth_fellow_into_the_party_does_not_leave_it_twice()
    {
        var (user, f) = await NewPlayerWithFellowsAsync("fourth-moved");
        await server.CallGatewayAsync(302, SetupFlagged(3, f[0], f[1], f[2], f[3]), user);

        await server.CallGatewayAsync(302, Setup(3, f[3], f[1], f[2]), user);
        JsonObject loaded = await server.CallGatewayAsync(60, new { user_no = user, party_slots = 4 }, user);

        Assert.Equal([f[3], f[1], f[2]], Party(loaded, 3));
    }

    [Fact]
    public async Task A_stage_result_pays_three_allies_to_the_original_and_four_to_the_rebuild()
    {
        var (user, f) = await NewPlayerWithFellowsAsync("fourth-72");
        await server.CallGatewayAsync(302, SetupFlagged(3, f[0], f[1], f[2], f[3]), user);
        object Result(bool flagged) => flagged
            ? new { stage_no = 3000001, stage_mode = 1, stage_result = 1, reward_gold = 0, reward_items = Array.Empty<long>(), event_items = Array.Empty<long>(), party_slots = 4 }
            : new { stage_no = 3000001, stage_mode = 1, stage_result = 1, reward_gold = 0, reward_items = Array.Empty<long>(), event_items = Array.Empty<long>() };

        await server.CallGatewayAsync(71, new { stage_id = 3000001, stage_mode = 1 }, user);
        JsonObject original = await server.CallGatewayAsync(72, Result(false), user);
        await server.CallGatewayAsync(71, new { stage_id = 3000001, stage_mode = 1 }, user);
        JsonObject rebuild = await server.CallGatewayAsync(72, Result(true), user);

        Assert.Equal(3, original["fellow_info"]!.AsArray().Count);
        JsonArray four = rebuild["fellow_info"]!.AsArray();
        Assert.Equal(4, four.Count);
        Assert.Equal([f[0], f[1], f[2], f[3]], four.Select(info => info!["fellow_no"]!.GetValue<long>()));
    }

    [Fact]
    public async Task A_fellow_in_the_fourth_slot_cannot_be_sold_by_either_client()
    {
        var (user, f) = await NewPlayerWithFellowsAsync("fourth-sell");
        await server.CallGatewayAsync(302, SetupFlagged(3, f[0], f[1], f[2], f[3]), user);

        JsonObject reply = await server.CallGatewayAsync(306, new { sell_fellows = new[] { f[3] } }, user);
        JsonObject flagged = await server.CallGatewayAsync(306, new { sell_fellows = new[] { f[3] }, party_slots = 4 }, user);

        Assert.Equal([f[3]], reply["fail_list"]!.AsArray().Select(n => n!.GetValue<long>()));
        Assert.Empty(reply["success_list"]!.AsArray());
        Assert.Equal([f[3]], flagged["fail_list"]!.AsArray().Select(n => n!.GetValue<long>()));
    }
}
