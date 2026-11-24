using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public sealed class MissionReputationTests(ServerFixture server)
{
    private static readonly int[] DuplicateMissionIds = [6210006, 6210006];
    private static readonly int[] SingleMissionId = [6210006];
    private static readonly int[] FailureMissionIds = [6210006, 6210009];

    [Fact]
    public async Task Successful_missions_persist_browser_reputation_once_and_isolate_accounts()
    {
        long user = await NewPlayerAsync("mission-reputation-success");
        long other = await NewPlayerAsync("mission-reputation-other");
        server.Execute($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,end_time,success_rate,rewards) VALUES({user},6210006,1,'','',1,100,'')");

        JsonObject claimed = await server.CallGatewayAsync(325, new { mission_id_list = DuplicateMissionIds, use_cash = 0 }, user);
        Assert.Single(claimed["success_fellow_mission_infos"]!.AsArray());
        Assert.Equal(2, claimed["user_reputation_info"]!["contry_reputation1"]!.GetValue<long>());
        Assert.Equal(11, claimed["user_reputation_info"]!.AsObject().Count);
        Assert.Equal(2, server.Scalar($"SELECT amount FROM fellow_mission_reputation WHERE user_no={user} AND country=1"));

        JsonObject repeated = await server.CallGatewayAsync(325, new { mission_id_list = SingleMissionId, use_cash = 0 }, user);
        Assert.Empty(repeated["success_fellow_mission_infos"]!.AsArray());
        Assert.Equal(2, repeated["user_reputation_info"]!["contry_reputation1"]!.GetValue<long>());
        JsonObject reopened = await server.CallGatewayAsync(322, new { }, user);
        Assert.Equal(2, reopened["user_reputation_info"]!["contry_reputation1"]!.GetValue<long>());
        JsonObject untouched = await server.CallGatewayAsync(322, new { }, other);
        Assert.All(untouched["user_reputation_info"]!.AsObject(), pair => Assert.Equal(0, pair.Value!.GetValue<long>()));
    }

    [Fact]
    public async Task Failed_and_unfinished_missions_never_award_reputation()
    {
        long user = await NewPlayerAsync("mission-reputation-failure");
        server.Execute($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,end_time,success_rate,rewards) VALUES({user},6210006,1,'','',1,0,''),({user},6210009,1,'','',4102444800,100,'')");
        JsonObject result = await server.CallGatewayAsync(325, new { mission_id_list = FailureMissionIds, use_cash = 0 }, user);
        Assert.Single(result["fail_fellow_mission_infos"]!.AsArray());
        Assert.Empty(result["success_fellow_mission_infos"]!.AsArray());
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow_mission_reputation WHERE user_no={user}"));
        Assert.All(result["user_reputation_info"]!.AsObject(), pair => Assert.Equal(0, pair.Value!.GetValue<long>()));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow_mission WHERE user_no={user} AND mission_id=6210009"));
    }

    private async Task<long> NewPlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        return user;
    }
}
