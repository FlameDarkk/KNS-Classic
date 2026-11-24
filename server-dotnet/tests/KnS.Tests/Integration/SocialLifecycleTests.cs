using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public class SocialLifecycleTests(ServerFixture server)
{
    private static readonly long[] MissingAllies = [999999];
    private static readonly long[] OneSoldier = [1];
    private static readonly long[] TwoBestSoldiers = [5, 5];
    private static readonly long[] ThreeBestSoldiers = [5, 5, 5];
    private static readonly long[] OneAndFive = [1, 5];
    private static readonly long[] SpecialMissionAllies = [123];
    private async Task<long> PlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        return user;
    }

    [Fact]
    public async Task Refused_mission_start_never_charges_for_mercenaries()
    {
        long user = await PlayerAsync("mission-no-charge");
        server.Execute($"UPDATE gameuser SET cash=100 WHERE user_no={user}");
        JsonObject reply = await server.CallGatewayAsync(324,
            new { mission_id = -1, fellow_no_list = MissingAllies, soldier_id_list = OneSoldier }, user);
        Assert.NotEqual(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(100, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task Mission_start_respects_browser_ally_slot_count_and_does_not_spend_on_refusal()
    {
        const long mission = 6210141; // Browser row +8 is 1; the unrelated c5 value is 4.
        long user = await PlayerAsync("mission-slot-limit");
        server.Execute($"UPDATE gameuser SET cash=100 WHERE user_no={user}");
        server.Execute($"INSERT INTO fellow(user_no,fellow_id) VALUES({user},100001)");
        long first = server.Scalar($"SELECT MAX(rowid_alias) FROM fellow WHERE user_no={user}") + 7000;
        server.Execute($"INSERT INTO fellow(user_no,fellow_id) VALUES({user},100011)");
        long second = server.Scalar($"SELECT MAX(rowid_alias) FROM fellow WHERE user_no={user}") + 7000;
        server.Execute($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,rewards) VALUES({user},{mission},0,'','','')");
        JsonObject refused = await server.CallGatewayAsync(324, new { mission_id = mission, fellow_no_list = new[] { first, second }, soldier_id_list = OneSoldier }, user);
        Assert.NotEqual(0, refused["errno"]!.GetValue<long>());
        Assert.Equal(100, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(0, server.Scalar($"SELECT status FROM fellow_mission WHERE user_no={user} AND mission_id={mission}"));
        JsonObject nobody = await server.CallGatewayAsync(324, new { mission_id = mission, fellow_no_list = Array.Empty<long>(), soldier_id_list = Array.Empty<long>() }, user);
        Assert.NotEqual(0, nobody["errno"]!.GetValue<long>());
        JsonObject chosen = await server.CallGatewayAsync(324, new { mission_id = mission, fellow_no_list = new[] { second }, soldier_id_list = Array.Empty<long>() }, user);
        Assert.Equal(0, chosen["errno"]!.GetValue<long>());
        Assert.Equal(second, chosen["fellow_mission_info"]!["fellow_no1"]!.GetValue<long>());
        Assert.Equal(0, chosen["fellow_mission_info"]!["fellow_no2"]!.GetValue<long>());
    }

    [Fact]
    public async Task Mercenary_only_start_deploys_no_unselected_ally_and_includes_mercenary_bonus()
    {
        const long mission = 6210141;
        long user = await PlayerAsync("mission-mercenary-only");
        server.Execute($"UPDATE gameuser SET cash=100 WHERE user_no={user}");
        server.Execute($"INSERT INTO fellow(user_no,fellow_id,level) VALUES({user},100001,30)");
        server.Execute($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,rewards) VALUES({user},{mission},0,'','','')");
        JsonObject started = await server.CallGatewayAsync(324, new { mission_id = mission, fellow_no_list = Array.Empty<long>(), soldier_id_list = TwoBestSoldiers }, user);
        Assert.Equal(0, started["errno"]!.GetValue<long>());
        Assert.Equal(0, started["fellow_mission_info"]!["fellow_no1"]!.GetValue<long>());
        Assert.Equal(2, started["fellow_mission_info"]!["soldier_count5"]!.GetValue<long>());
        // Two type-5 mercenaries: 2 x 25 (GetSuccessRate@0x563d8), no ally score.
        Assert.Equal(50, started["fellow_mission_info"]!["success_rate"]!.GetValue<long>());
        // Both are among the three free type-5 mercenaries (Scene_PointerReleaseEvent_SC_POPUP_FELLOW_MISSION @0x5e65c).
        Assert.Equal(100, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(2, started["free_soldier_use_count"]!.GetValue<long>());
        Assert.Equal(0, server.Scalar($"SELECT LENGTH(fellows) FROM fellow_mission WHERE user_no={user} AND mission_id={mission}"));
    }

    [Fact]
    public async Task Free_type5_mercenaries_start_without_gems_then_cost_their_native_price()
    {
        const long first = 6210141, second = 6210142;
        long user = await PlayerAsync("mission-free-mercenaries");
        server.Execute($"UPDATE gameuser SET cash=0 WHERE user_no={user}");
        server.Execute($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,rewards) VALUES({user},{first},0,'','',''),({user},{second},0,'','','')");
        // Three free type-5 mercenaries need no Gems: the client sends the start without asking.
        JsonObject free = await server.CallGatewayAsync(324, new { mission_id = first, fellow_no_list = Array.Empty<long>(), soldier_id_list = ThreeBestSoldiers }, user);
        Assert.Equal(0, free["errno"]!.GetValue<long>());
        Assert.Equal(3, free["free_soldier_use_count"]!.GetValue<long>());
        // The free ones are used up: one type-1 (10) and one type-5 (50) now cost 60 Gems.
        JsonObject poor = await server.CallGatewayAsync(324, new { mission_id = second, fellow_no_list = Array.Empty<long>(), soldier_id_list = OneAndFive }, user);
        Assert.NotEqual(0, poor["errno"]!.GetValue<long>());
        server.Execute($"UPDATE gameuser SET cash=70 WHERE user_no={user}");
        JsonObject paid = await server.CallGatewayAsync(324, new { mission_id = second, fellow_no_list = Array.Empty<long>(), soldier_id_list = OneAndFive }, user);
        Assert.Equal(0, paid["errno"]!.GetValue<long>());
        Assert.Equal(10, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task Free_mercenaries_reset_the_next_day()
    {
        const long first = 6210141, second = 6210142;
        long user = await PlayerAsync("mission-free-mercenaries-daily");
        server.Execute($"UPDATE gameuser SET cash=0 WHERE user_no={user}");
        server.Execute($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,rewards) VALUES({user},{first},0,'','',''),({user},{second},0,'','','')");
        JsonObject free = await server.CallGatewayAsync(324, new { mission_id = first, fellow_no_list = Array.Empty<long>(), soldier_id_list = ThreeBestSoldiers }, user);
        Assert.Equal(3, free["free_soldier_use_count"]!.GetValue<long>());
        Assert.Equal(3, (await server.CallGatewayAsync(322, new { }, user))["free_soldier_use_count"]!.GetValue<long>());
        // The count belongs to a UTC day: move it to yesterday.
        server.Execute($"UPDATE fellow_mission_state SET free_soldiers_day=free_soldiers_day-1 WHERE user_no={user}");
        Assert.Equal(0, (await server.CallGatewayAsync(322, new { }, user))["free_soldier_use_count"]!.GetValue<long>());
        JsonObject again = await server.CallGatewayAsync(324, new { mission_id = second, fellow_no_list = Array.Empty<long>(), soldier_id_list = ThreeBestSoldiers }, user);
        Assert.Equal(0, again["errno"]!.GetValue<long>());
        Assert.Equal(3, again["free_soldier_use_count"]!.GetValue<long>());
        Assert.Equal(0, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task Unsupported_special_mission_requests_do_not_change_running_missions()
    {
        long user = await PlayerAsync("special-mission-refusal");
        server.Execute($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,end_time,success_rate,rewards) VALUES({user},6210056,1,'123','','2000000000',75,'')");
        foreach (int protocol in new[] { 326, 327 })
        {
            JsonObject reply = await server.CallGatewayAsync(protocol, new { mission_id = 6210056, fellow_no_list = SpecialMissionAllies }, user);
            Assert.NotEqual(0, reply["errno"]!.GetValue<long>());
            Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow_mission WHERE user_no={user} AND mission_id=6210056 AND status=1 AND end_time=2000000000"));
        }
    }

    [Fact]
    public async Task A_full_friend_list_does_not_report_unsent_requests_as_successful()
    {
        long full = await PlayerAsync("friend-full"), target = await PlayerAsync("friend-full-target");
        for (var i = 0; i < 30; i++)
        {
            server.Execute($"INSERT INTO friend_link(user_no,friend_no,status,created) VALUES({full},{900000 + i},2,1)");
        }
        JsonObject reply = await server.CallGatewayAsync(801, new { friend_list = new[] { target } }, full);
        Assert.Empty(reply["success_list"]!.AsArray());
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM friend_link WHERE user_no={full} AND friend_no={target}"));
        await server.CallGatewayAsync(801, new { friend_list = new[] { full } }, target);
        JsonObject accepted = await server.CallGatewayAsync(111, new { friend_list = new[] { full } }, target);
        Assert.Empty(accepted["friend_info"]!.AsArray());
    }

    [Fact]
    public async Task Friend_acceptance_checks_both_players_capacity_and_keeps_the_request_pending()
    {
        long requester = await PlayerAsync("friend-cap-requester"), accepter = await PlayerAsync("friend-cap-accepter");
        await server.CallGatewayAsync(801, new { friend_list = new[] { accepter } }, requester);
        for (var i = 0; i < 30; i++)
        {
            server.Execute($"INSERT INTO friend_link(user_no,friend_no,status,created) VALUES({requester},{910000 + i},2,1)");
        }
        JsonObject accepted = await server.CallGatewayAsync(111, new { friend_list = new[] { requester } }, accepter);
        Assert.Empty(accepted["friend_info"]!.AsArray());
        Assert.Equal(1, server.Scalar($"SELECT status FROM friend_link WHERE user_no={requester} AND friend_no={accepter}"));
    }

    [Fact]
    public async Task Duplicate_party_allies_are_refused_without_changing_the_old_setup()
    {
        long user = await PlayerAsync("party-duplicate");
        server.Execute($"INSERT INTO fellow(user_no,fellow_id) VALUES({user},100001)");
        long ally = server.Scalar($"SELECT MAX(rowid_alias) FROM fellow WHERE user_no={user}") + 7000;
        await server.CallGatewayAsync(302, new { type = 3, fellows = new[] { new { fellow_no = ally, fellow_position = 0 } } }, user);
        JsonObject reply = await server.CallGatewayAsync(302, new
        {
            type = 3,
            fellows = new[] { new { fellow_no = ally, fellow_position = 0 }, new { fellow_no = ally, fellow_position = 1 } },
        }, user);
        Assert.NotEqual(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow_active WHERE user_no={user} AND fellow_no={ally}"));
    }
}
