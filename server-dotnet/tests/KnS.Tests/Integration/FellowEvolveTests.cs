using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

/// <summary>Ally Evolution (309) with angel materials and talent changes (319).</summary>
[Collection(SharedServer.Name)]
public class FellowEvolveTests(ServerFixture server)
{
    private const long FellowOffset = 7_000;
    private const long Emma = 100211, Dominion = 110006, Aruel = 110001, Roina = 100001;

    private async Task<long> NewPlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        server.Execute($"UPDATE gameuser SET gold=1000000, cash=100 WHERE user_no={user}");
        return user;
    }

    private long Grant(long user, long fellowId, long level = 1, long upgrade = 0)
    {
        server.Execute($"INSERT INTO fellow(user_no, fellow_id, level, upgrade) VALUES({user}, {fellowId}, {level}, {upgrade})");
        return server.Scalar("SELECT MAX(rowid_alias) FROM fellow") + FellowOffset;
    }

    [Fact]
    public async Task Six_star_evolves_four_times_with_same_grade_angels_and_reaches_level_cap_30()
    {
        long user = await NewPlayerAsync("evolve-emma");
        long emma = Grant(user, Emma, level: 20, upgrade: 5);
        for (int i = 1; i <= 4; i++)
        {
            long angel = Grant(user, Dominion);
            JsonObject reply = await server.CallGatewayAsync(309, new { fellow_no = emma, used_fellow_no = angel }, user);
            Assert.Equal(0, reply["errno"]!.GetValue<long>());
            Assert.Equal(1, reply["upgrade_result"]!.GetValue<long>());
            Assert.Equal(angel, reply["used_fellow_no"]!.GetValue<long>());
            Assert.Equal(i, reply["fellow_info"]!["fellow_awaken_level"]!.GetValue<long>());
            // Emma's evolution skills: 2500601, 2530501, then MAX Level +5 twice.
            Assert.Equal(i <= 2 ? 20 : i == 3 ? 25 : 30, reply["fellow_info"]!["fellow_max_level"]!.GetValue<long>());
            Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE rowid_alias={angel - FellowOffset}"));
        }

        Assert.Equal(1000000 - 4 * 50000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        long fifth = Grant(user, Dominion);
        Assert.NotEqual(0, (await server.CallGatewayAsync(309, new { fellow_no = emma, used_fellow_no = fifth }, user))["errno"]!.GetValue<long>());
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE rowid_alias={fifth - FellowOffset}"));
    }

    [Fact]
    public async Task Evolution_refuses_non_angel_material_and_allies_without_evolutions()
    {
        long user = await NewPlayerAsync("evolve-refused");
        long emma = Grant(user, Emma, level: 20, upgrade: 5);
        long roina = Grant(user, Roina);
        long angel = Grant(user, Aruel);
        Assert.NotEqual(0, (await server.CallGatewayAsync(309, new { fellow_no = emma, used_fellow_no = roina }, user))["errno"]!.GetValue<long>());
        Assert.NotEqual(0, (await server.CallGatewayAsync(309, new { fellow_no = roina, used_fellow_no = angel }, user))["errno"]!.GetValue<long>());
        Assert.Equal(1000000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.Equal(3, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user}"));
    }

    [Fact]
    public async Task Talent_change_costs_ten_gems_and_rolls_a_levelled_skill_of_the_ally()
    {
        long user = await NewPlayerAsync("talent-change");
        long emma = Grant(user, Emma);
        JsonObject loaded = await server.CallGatewayAsync(60, new { }, user);
        JsonNode info = loaded["fellow_info"]!.AsArray().Single(row => row!["fellow_no"]!.GetValue<long>() == emma)!;
        Assert.Equal(6220011, info["fellow_mission_skill01"]!.GetValue<long>());
        Assert.Equal(10, info["next_fellow_attribute_reset_info"]!["AttributeResetValue"]!.GetValue<long>());

        JsonObject reply = await server.CallGatewayAsync(319, new { fellow_no = emma, attribute_no = 1 }, user);
        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        long talent = reply["fellow_info"]!["fellow_mission_skill01"]!.GetValue<long>();
        long[] families = [622001, 622002, 622029, 622033, 622036];
        Assert.Contains(talent / 10, families);
        Assert.NotEqual(622002, talent / 10); // never the other talent's family
        Assert.InRange(talent % 10, 1, 8);
        Assert.Equal(90, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(talent, server.Scalar($"SELECT talent1 FROM fellow WHERE rowid_alias={emma - FellowOffset}"));
        Assert.NotEqual(0, (await server.CallGatewayAsync(319, new { fellow_no = emma, attribute_no = 3 }, user))["errno"]!.GetValue<long>());
    }

    [Fact]
    public async Task Max_level_book_reward_pays_ten_gems_once()
    {
        long user = await NewPlayerAsync("book-reward");
        Grant(user, Dominion, level: 99);
        server.Execute($"INSERT OR IGNORE INTO fellow_book(user_no, fellow_id) VALUES({user}, {Dominion})");

        JsonObject reply = await server.CallGatewayAsync(308, new { fellow_id = Dominion }, user);
        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(1, reply["fellow_book_info"]!["recv_reward"]!.GetValue<long>());
        Assert.Equal(110, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));

        Assert.NotEqual(0, (await server.CallGatewayAsync(308, new { fellow_id = Dominion }, user))["errno"]!.GetValue<long>());
        Assert.Equal(110, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task Book_reward_remains_claimable_after_the_ally_is_consumed()
    {
        long user = await NewPlayerAsync("book-reward-consumed");
        server.Execute($"INSERT INTO fellow_book(user_no, fellow_id, level) VALUES({user}, {Dominion}, 1)");
        JsonObject reply = await server.CallGatewayAsync(308, new { fellow_id = Dominion }, user);
        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(1, reply["fellow_book_info"]!["recv_reward"]!.GetValue<long>());
        Assert.Equal(110, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(1, server.Scalar($"SELECT recv_reward FROM fellow_book WHERE user_no={user} AND fellow_id={Dominion}"));
        JsonObject loaded = await server.CallGatewayAsync(60, new { }, user);
        Assert.Equal(1, loaded["fellow_book_info"]!.AsArray().Single(row => row!["fellow_id"]!.GetValue<long>() == Dominion)!["recv_reward"]!.GetValue<long>());
        Assert.NotEqual(0, (await server.CallGatewayAsync(308, new { fellow_id = Dominion }, user))["errno"]!.GetValue<long>());
        Assert.Equal(110, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task Book_reward_uses_base_collection_cap_after_evolution()
    {
        long user = await NewPlayerAsync("book-reward-evolved");
        long emma = Grant(user, Emma, level: 20);
        server.Execute($"UPDATE fellow SET max_level_bonus=5 WHERE rowid_alias={emma - FellowOffset}");
        JsonObject reply = await server.CallGatewayAsync(308, new { fellow_id = Emma }, user);
        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(1, reply["fellow_book_info"]!["recv_reward"]!.GetValue<long>());
        Assert.Equal(110, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task Book_reward_refused_below_max_level()
    {
        long user = await NewPlayerAsync("book-reward-low");
        Grant(user, Emma);
        Assert.NotEqual(0, (await server.CallGatewayAsync(308, new { fellow_id = Emma }, user))["errno"]!.GetValue<long>());
        Assert.Equal(100, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
    }
}
