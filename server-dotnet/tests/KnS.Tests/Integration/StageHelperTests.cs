using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

/// <summary>Protocol 71 sends the chosen helper's hero as friend_detail_info (ParsingDataToDungeonFriendDetail@0x14cfe4).</summary>
[Collection(SharedServer.Name)]
public sealed class StageHelperTests(ServerFixture server)
{
    private const long Stage = 3001004;

    [Fact]
    public async Task Stage_start_carries_the_helpers_hero()
    {
        long user = await PlayerAsync("helper-caller");
        long helper = await PlayerAsync("helper-friend");
        JsonObject start = await server.CallGatewayAsync(71, new { friend_type = 1, stage_id = Stage, stage_mode = 1, friend_user_no = helper }, user);
        Assert.Equal(0, start["errno"]!.GetValue<long>());
        JsonObject detail = start["friend_detail_info"]!.AsObject();
        Assert.Equal(helper, detail["friend_user_no"]!.GetValue<long>());
        Assert.True(detail["user_level"]!.GetValue<long>() >= 1);
        JsonObject hero = detail["hero_info"]!.AsObject();
        Assert.True(hero["hero_pcid"]!.GetValue<long>() > 0);
    }

    [Fact]
    public async Task No_helper_leaves_the_block_out()
    {
        long user = await PlayerAsync("helper-none");
        JsonObject start = await server.CallGatewayAsync(71, new { friend_type = 0, stage_id = Stage, stage_mode = 1, friend_user_no = 0 }, user);
        Assert.Equal(0, start["errno"]!.GetValue<long>());
        Assert.Null(start["friend_detail_info"]);
    }

    private async Task<long> PlayerAsync(string name)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = name }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        return user;
    }
}
