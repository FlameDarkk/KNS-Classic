using System.Globalization;
using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public sealed class OnlinePresenceTests(ServerFixture server)
{
    [Fact]
    public async Task Authenticated_poll_keeps_idle_player_online_without_game_requests_and_expires_after_polling_stops()
    {
        JsonObject login = await server.CallGatewayAsync(51, new { hub_did = "online-heartbeat" });
        long user = login["user_no"]!.GetValue<long>();
        string key = login["session_key"]!.GetValue<string>();
        server.Execute($"UPDATE gameuser SET nickname='HeartbeatPlayer',last_seen=0 WHERE user_no={user}");
        Assert.DoesNotContain("HeartbeatPlayer", Names(await PollAsync()));

        for (int i = 0; i < 2; i++)
        {
            JsonObject reply = await PollAsync(user, key);
            Assert.Single(Names(reply), name => name == "HeartbeatPlayer");
            Assert.True(server.Scalar($"SELECT last_seen FROM gameuser WHERE user_no={user}") > 0);
            // Simulate another idle period longer than the presence window, with no game request.
            server.Execute($"UPDATE gameuser SET last_seen=0 WHERE user_no={user}");
        }

        Assert.DoesNotContain("HeartbeatPlayer", Names(await PollAsync()));
    }

    [Fact]
    public async Task Missing_wrong_and_other_players_session_keys_cannot_refresh_presence()
    {
        JsonObject login = await server.CallGatewayAsync(51, new { hub_did = "online-auth-owner" });
        JsonObject other = await server.CallGatewayAsync(51, new { hub_did = "online-auth-other" });
        long user = login["user_no"]!.GetValue<long>();
        server.Execute($"UPDATE gameuser SET nickname='OfflineOwner',last_seen=0 WHERE user_no={user}");
        foreach (string? key in new[] { null, "invalid", other["session_key"]!.GetValue<string>() })
        {
            Assert.DoesNotContain("OfflineOwner", Names(await PollAsync(user, key)));
            Assert.Equal(0, server.Scalar($"SELECT last_seen FROM gameuser WHERE user_no={user}"));
        }
        Assert.Contains("OfflineOwner", Names(await PollAsync(user, login["session_key"]!.GetValue<string>())));
    }

    [Fact]
    public async Task Get_with_credentials_is_still_a_read_only_public_poll()
    {
        JsonObject login = await server.CallGatewayAsync(51, new { hub_did = "online-public-get" });
        long user = login["user_no"]!.GetValue<long>();
        server.Execute($"UPDATE gameuser SET last_seen=0 WHERE user_no={user}");
        await PollAsync(user, login["session_key"]!.GetValue<string>(), HttpMethod.Get);
        Assert.Equal(0, server.Scalar($"SELECT last_seen FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task Roster_includes_one_in_game_name_per_active_account()
    {
        var users = new long[7];
        for (int i = 0; i < users.Length; i++)
        {
            JsonObject login = await server.CallGatewayAsync(51, new { hub_did = $"online-roster-{i}" });
            users[i] = login["user_no"]!.GetValue<long>();
        }

        server.Execute($"""
            UPDATE gameuser
            SET nickname=CASE WHEN user_no={users[0]} THEN 'RosterTestAlpha'
                              WHEN user_no={users[1]} THEN 'RosterTestBeta'
                              WHEN user_no={users[2]} THEN '   ' ELSE '' END,
                last_seen=CASE WHEN user_no={users[6]} THEN 0 ELSE CAST(strftime('%s','now') AS INTEGER) END
            WHERE user_no IN ({string.Join(",", users)})
            """);

        JsonObject reply = await PollAsync();
        string[] names = Names(reply).ToArray();
        Assert.Equal(reply["online"]!.GetValue<long>(), names.LongLength);
        Assert.Contains("RosterTestAlpha", names);
        Assert.Contains("RosterTestBeta", names);
        foreach (long user in users[2..6])
            Assert.Contains($"Squire{user}", names);
        Assert.DoesNotContain($"Squire{users[6]}", names);

        server.Execute($"UPDATE gameuser SET nickname='RosterTestAlpha' WHERE user_no={users[1]}");
        JsonObject duplicateReply = await PollAsync();
        string[] duplicateNames = Names(duplicateReply).ToArray();
        Assert.Equal(duplicateReply["online"]!.GetValue<long>(), duplicateNames.LongLength);
        Assert.Equal(2, duplicateNames.Count(name => name == "RosterTestAlpha"));
    }

    private async Task<JsonObject> PollAsync(long user = 0, string? key = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/online");
        if (user != 0) request.Headers.Add("USERNO", user.ToString(CultureInfo.InvariantCulture));
        if (key is not null) request.Headers.Add("SESSIONKEY", key);
        using HttpResponseMessage response = await server.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static IEnumerable<string> Names(JsonObject reply) =>
        reply["players"]!.AsArray().Select(name => name!.GetValue<string>());
}
