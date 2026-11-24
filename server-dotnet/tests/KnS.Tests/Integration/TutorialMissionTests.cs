using KnS.Data;
using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public sealed class TutorialMissionTests(ServerFixture server)
{
    [Fact]
    public async Task Tutorial_preserves_selection_reserves_allies_and_claims_early_once_without_rewards()
    {
        long user = await NewPlayerAsync("tutorial-mission-claim");
        server.Execute($"INSERT INTO fellow(user_no,fellow_id,level) VALUES({user},100001,5)");
        long ally = server.Scalar($"SELECT MAX(rowid_alias)+{WireIdentifiers.FellowNumberOffset} FROM fellow WHERE user_no={user}");
        long gems = server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}");
        JsonObject board = await server.CallGatewayAsync(322, new { }, user);
        Assert.DoesNotContain(board["fellow_mission_infos"]!.AsArray(), row => row!["mission_id"]!.GetValue<long>() == 6220000);
        long ordinary = board["fellow_mission_infos"]![0]!["mission_id"]!.GetValue<long>();
        JsonObject start = await server.CallGatewayAsync(326, new { mission_id = 6220000, fellow_no_list = new[] { ally } }, user);
        Assert.Equal(0, start["errno"]!.GetValue<long>());
        Assert.Equal(ally, start["fellow_mission_info"]!["fellow_no1"]!.GetValue<long>());
        Assert.True(start["fellow_mission_info"]!["mission_end_remain_time"]!.GetValue<long>() > 3500);
        JsonObject reopened = await server.CallGatewayAsync(322, new { }, user);
        Assert.Single(reopened["fellow_mission_infos"]!.AsArray().Where(row => row!["mission_id"]!.GetValue<long>() == 6220000));
        JsonObject blocked = await server.CallGatewayAsync(324, new { mission_id = ordinary, fellow_no_list = new[] { ally } }, user);
        Assert.NotEqual(0, blocked["errno"]!.GetValue<long>());
        JsonObject result = await server.CallGatewayAsync(327, new { mission_id = 6220000 }, user);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Equal(0, result["fellow_mission_info"]!["mission_end_remain_time"]!.GetValue<long>());
        for (int slot = 1; slot <= 6; slot++)
        {
            Assert.Equal(0, result["fellow_mission_info"]![$"reward_id{slot}"]!.GetValue<long>());
        }

        Assert.Equal(3, server.Scalar($"SELECT status FROM fellow_mission_tutorial WHERE user_no={user}"));
        Assert.Equal(gems, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow_mission_reputation WHERE user_no={user}"));
        Assert.Equal(8, server.Scalar($"SELECT COUNT(*) FROM fellow_mission WHERE user_no={user}"));
        JsonObject duplicate = await server.CallGatewayAsync(327, new { mission_id = 6220000 }, user);
        Assert.NotEqual(0, duplicate["errno"]!.GetValue<long>());
        JsonObject released = await server.CallGatewayAsync(324, new { mission_id = ordinary, fellow_no_list = new[] { ally } }, user);
        Assert.Equal(0, released["errno"]!.GetValue<long>());
    }

    [Fact]
    public async Task Tutorial_rejects_foreign_duplicate_overfull_and_normal_mission_requests()
    {
        long user = await NewPlayerAsync("tutorial-mission-invalid");
        long other = await NewPlayerAsync("tutorial-mission-foreign");
        server.Execute($"INSERT INTO fellow(user_no,fellow_id,level) VALUES({other},100001,5)");
        long foreign = server.Scalar($"SELECT MAX(rowid_alias)+{WireIdentifiers.FellowNumberOffset} FROM fellow WHERE user_no={other}");
        JsonObject refused = await server.CallGatewayAsync(326, new { mission_id = 6220000, fellow_no_list = new[] { foreign } }, user);
        Assert.NotEqual(0, refused["errno"]!.GetValue<long>());
        server.Execute($"INSERT INTO fellow(user_no,fellow_id,level) VALUES({user},100001,5)");
        long ally = server.Scalar($"SELECT MAX(rowid_alias)+{WireIdentifiers.FellowNumberOffset} FROM fellow WHERE user_no={user}");
        long[] duplicate = [ally, ally];
        long[] tooMany = [ally, ally + 1, ally + 2, ally + 3];
        foreach (long[] selection in new[] { duplicate, tooMany })
        {
            JsonObject invalid = await server.CallGatewayAsync(326, new { mission_id = 6220000, fellow_no_list = selection }, user);
            Assert.NotEqual(0, invalid["errno"]!.GetValue<long>());
        }

        JsonObject board = await server.CallGatewayAsync(322, new { }, user);
        long ordinary = board["fellow_mission_infos"]![0]!["mission_id"]!.GetValue<long>();
        JsonObject normalStart = await server.CallGatewayAsync(326, new { mission_id = ordinary, fellow_no_list = new[] { ally } }, user);
        JsonObject normalClaim = await server.CallGatewayAsync(327, new { mission_id = ordinary }, user);
        Assert.NotEqual(0, normalStart["errno"]!.GetValue<long>());
        Assert.NotEqual(0, normalClaim["errno"]!.GetValue<long>());
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow_mission_tutorial WHERE user_no={user}"));
        Assert.Equal(8, server.Scalar($"SELECT COUNT(*) FROM fellow_mission WHERE user_no={user} AND status=0"));
    }

    private async Task<long> NewPlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        return user;
    }
}
