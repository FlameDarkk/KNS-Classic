using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

/// <summary>Protocol 123 ("Collect" in Achievements In progress): the reply must name the collected rule, or the client keeps the button.</summary>
[Collection(SharedServer.Name)]
public class AchievementClaimTests(ServerFixture server)
{
    private const long ReachLv5 = 4000080;
    private const long ReachLv6 = 4000081;
    private const long ReachLv8 = 4000082;
    private const long ReachLv10 = 4000001;

    [Fact]
    public async Task Collect_grants_each_level_rule_once_in_chain_order_and_reports_it()
    {
        long user = await NewPlayerAsync("achievement-claim");

        server.Execute($"UPDATE gameuser SET level=1 WHERE user_no={user}");
        JsonObject early = await server.CallGatewayAsync(123, new { achievement_id = ReachLv5 }, user);
        Assert.NotEqual(0, Errno(early));

        server.Execute($"UPDATE gameuser SET level=5 WHERE user_no={user}");
        server.Execute($"DELETE FROM player_stamina WHERE user_no={user}");
        server.Execute($"INSERT INTO player_stamina(user_no, stamina, last_gain) VALUES({user}, 40, {DateTimeOffset.UtcNow.ToUnixTimeSeconds()})");
        JsonObject first = await server.CallGatewayAsync(123, new { achievement_id = ReachLv5 }, user);
        Assert.Equal(0, Errno(first));
        Assert.Equal(ReachLv5, first["achievement_reward_info"]!["achievement_id"]!.GetValue<long>());
        Assert.NotEqual("", first["achievement_reward_info"]!["reg_date"]!.GetValue<string>());
        long stamina = server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}");
        Assert.Equal(60, stamina); // Reach Lv. 5 pays 20 stamina
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM achievement_claim WHERE user_no={user} AND achievement_id={ReachLv5}"));

        // A repeat claim re-sends the record but pays nothing.
        JsonObject again = await server.CallGatewayAsync(123, new { achievement_id = ReachLv5 }, user);
        Assert.Equal(0, Errno(again));
        Assert.Equal(ReachLv5, again["achievement_reward_info"]!["achievement_id"]!.GetValue<long>());
        Assert.Equal(stamina, server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}"));

        JsonObject skipped = await server.CallGatewayAsync(123, new { achievement_id = ReachLv8 }, user);
        Assert.NotEqual(0, Errno(skipped));

        JsonObject notYet = await server.CallGatewayAsync(123, new { achievement_id = ReachLv6 }, user);
        Assert.NotEqual(0, Errno(notYet));

        server.Execute($"UPDATE gameuser SET level=6 WHERE user_no={user}");
        JsonObject second = await server.CallGatewayAsync(123, new { achievement_id = ReachLv6 }, user);
        Assert.Equal(0, Errno(second));
        Assert.Equal(ReachLv6, second["achievement_reward_info"]!["achievement_id"]!.GetValue<long>());
        // Reach Lv. 6 pays ally 100322: owned, listed in the reply, not a counted item.
        Assert.IsType<JsonObject>(second["fellow_info"]);
        Assert.Equal(100322, second["fellow_info"]!["fellow_id"]!.GetValue<long>());
        Assert.Equal(100322, second["fellow_book_info"]!["fellow_id"]!.GetValue<long>());
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id=100322"));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM user_goods WHERE user_no={user} AND item_id=100322"));
    }

    [Fact]
    public async Task Stage_clear_allies_use_single_objects_and_repeated_claims_never_pay_again()
    {
        long user = await NewPlayerAsync("achievement-stage-allies");
        foreach (var (achievement, ally) in new[] { (4007198L, 100460L), (4007199L, 100444L) })
        {
            JsonObject reply = await server.CallGatewayAsync(123, new { achievement_id = achievement }, user);
            Assert.Equal(0, Errno(reply));
            Assert.IsType<JsonObject>(reply["fellow_info"]);
            Assert.IsType<JsonObject>(reply["fellow_book_info"]);
            Assert.Equal(ally, reply["fellow_info"]!["fellow_id"]!.GetValue<long>());
            Assert.Equal(ally, reply["fellow_book_info"]!["fellow_id"]!.GetValue<long>());
            Assert.True(reply["fellow_info"]!["fellow_no"]!.GetValue<long>() > 0);
            JsonObject again = await server.CallGatewayAsync(123, new { achievement_id = achievement }, user);
            Assert.Equal(0, Errno(again));
            Assert.Null(again["fellow_info"]);
            Assert.Null(again["fellow_book_info"]);
            Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id={ally}"));
        }
        JsonObject inventory = await server.CallGatewayAsync(60, new { user_no = user }, user);
        // Persistence is checked independently of the claim response and survives a new request.
        Assert.Equal(0, Errno(inventory));
        JsonArray allies = inventory["fellow_info"]!.AsArray();
        Assert.Single(allies, row => row!["fellow_id"]!.GetValue<long>() == 100444);
        Assert.Single(allies, row => row!["fellow_id"]!.GetValue<long>() == 100460);
        Assert.Equal(2, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id IN (100460,100444)"));
    }

    [Fact]
    public async Task Stamina_reward_lands_on_a_bar_with_an_old_refill_clock()
    {
        long user = await NewPlayerAsync("achievement-claim-stamina");
        server.Execute($"UPDATE gameuser SET level=7 WHERE user_no={user}");
        server.Execute($"DELETE FROM player_stamina WHERE user_no={user}");
        server.Execute($"INSERT INTO player_stamina(user_no, stamina, last_gain) VALUES({user}, 60, {DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1000})");
        JsonObject reply = await server.CallGatewayAsync(123, new { achievement_id = ReachLv5 }, user);
        Assert.Equal(0, Errno(reply));
        long shown = reply["update_item_etc_info"]!.AsArray().Single(row => row!["item_id"]!.GetValue<long>() == 1900014)!["count"]!.GetValue<long>();
        Assert.True(shown >= 80, $"stamina shown {shown}");
    }

    [Fact]
    public async Task Collect_refuses_unknown_rules()
    {
        long user = await NewPlayerAsync("achievement-claim-unknown");
        JsonObject reply = await server.CallGatewayAsync(123, new { achievement_id = 999999999 }, user);
        Assert.NotEqual(0, Errno(reply));
    }

    [Fact]
    public async Task Login_lists_restore_collected_rules_and_counters_after_a_reload()
    {
        long user = await NewPlayerAsync("achievement-login-lists");
        server.Execute($"UPDATE gameuser SET level=14 WHERE user_no={user}");
        Assert.Equal(0, Errno(await server.CallGatewayAsync(123, new { achievement_id = ReachLv5 }, user)));

        // Totals reported with any request are kept; a lower total never replaces a higher one.
        await server.CallGatewayAsync(116, new { friend_list = Array.Empty<long>(), achievement_list = new[] { new { type = 40051, count = 44 } } }, user);
        await server.CallGatewayAsync(116, new { friend_list = Array.Empty<long>(), achievement_list = new[] { new { type = 40051, count = 23 } } }, user);

        JsonObject collected = await server.CallGatewayAsync(122, new { }, user);
        Assert.Equal(0, Errno(collected));
        JsonArray rewards = collected["achievement_reward_infos"]!.AsArray();
        Assert.Single(rewards);
        Assert.Equal(ReachLv5, rewards[0]!["achievement_id"]!.GetValue<long>());
        Assert.NotEqual("", rewards[0]!["reg_date"]!.GetValue<string>());

        JsonObject doing = await server.CallGatewayAsync(121, new { }, user);
        Assert.Equal(0, Errno(doing));
        JsonArray infos = doing["achievement_infos"]!.AsArray();
        Assert.Contains(infos, i => i!["type"]!.GetValue<long>() == 40000 && i["count"]!.GetValue<long>() == 14);
        Assert.Contains(infos, i => i!["type"]!.GetValue<long>() == 40051 && i["count"]!.GetValue<long>() == 44);
    }

    private async Task<long> NewPlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        return user;
    }

    private static long Errno(JsonObject reply) => reply["errno"]?.GetValue<long>() ?? 0;
}
