using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

/// <summary>Protocol 1101: Altar Superb Evolution, Transform and Arena recipes.</summary>
[Collection(SharedServer.Name)]
public class AltarTests(ServerFixture server)
{
    private const long Gold = 1_000_000;
    private static readonly long[] Materials = [1901001, 1901002, 1901003, 1901004, 1901005, 1901006, 1901007];

    [Fact]
    public void Recipes_load_from_browser_data()
    {
        Assert.Equal(20, server.Scalar("SELECT COUNT(*) FROM master_altar_recipe"));
        Assert.Equal(1900008, server.Scalar("SELECT field_40 FROM master_altar_recipe WHERE recipe_id=105019"));
    }

    [Fact]
    public async Task Arena_recipe_merges_two_max_plus5_copies()
    {
        long user = await PlayerAsync("altar-arena");
        long a = AddAlly(user, 100498, maxed: true);
        long b = AddAlly(user, 100498, maxed: true);
        JsonObject reply = await CraftAsync(user, 100499, a, b);
        Assert.Equal(0, Errno(reply));
        Assert.Equal(100499, reply["fellow_info"]!["fellow_id"]!.GetValue<long>());
        Assert.Equal([a, b], reply["used_item"]!.AsArray().Select(n => n!.GetValue<long>()).Order().ToArray());
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id=100498"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id=100499"));
        Assert.Equal(Gold - 150_000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.Equal(1000 - 600, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1901007"));
    }

    [Fact]
    public async Task Superb_evolution_gives_the_recipe_ally()
    {
        long user = await PlayerAsync("altar-superb");
        long a = AddAlly(user, 105018, maxed: true);
        long b = AddAlly(user, 105018, maxed: true);
        JsonObject reply = await CraftAsync(user, 105019, a, b);
        Assert.Equal(0, Errno(reply));
        Assert.Equal(105019, reply["fellow_info"]!["fellow_id"]!.GetValue<long>());
    }

    [Fact]
    public async Task Six_star_transform_turns_one_ally_into_another_six_star()
    {
        long user = await PlayerAsync("altar-transform");
        long ally = AddAlly(user, 100499, maxed: false);
        JsonObject reply = await CraftAsync(user, 111111, ally, 0);
        Assert.Equal(0, Errno(reply));
        long result = reply["fellow_info"]!["fellow_id"]!.GetValue<long>();
        Assert.NotEqual(100499, result);
        Assert.Equal(6, server.Scalar($"SELECT grade FROM master_fellow WHERE fellow_id={result}"));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id=100499"));
        Assert.Equal(Gold - 100_000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.Equal(1000 - 200, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1901007"));
    }

    [Fact]
    public async Task Refusals_name_the_reason_and_keep_everything()
    {
        long user = await PlayerAsync("altar-refusal");
        long a = AddAlly(user, 105018, maxed: true);
        long weak = AddAlly(user, 105018, maxed: false);
        Assert.Equal(21011, Errno(await CraftAsync(user, 105019, a, weak)));   // not MAX +5
        Assert.Equal(21016, Errno(await CraftAsync(user, 105019, a, a)));      // same ally twice
        Assert.Equal(21016, Errno(await CraftAsync(user, 111111, a, 0)));      // 5★ in the 6★ transform
        server.Execute($"UPDATE gameuser SET gold=10 WHERE user_no={user}");
        long b = AddAlly(user, 105018, maxed: true);
        Assert.Equal(12004, Errno(await CraftAsync(user, 105019, a, b)));
        Assert.Equal(3, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id=105018"));
        Assert.Equal(1000, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1901007"));
    }

    private long AddAlly(long user, long fellowId, bool maxed)
    {
        long level = maxed ? server.Scalar($"SELECT max_level FROM master_fellow WHERE fellow_id={fellowId}") : 1;
        server.Execute($"INSERT INTO fellow(user_no,fellow_id,level,upgrade) VALUES({user},{fellowId},{level},{(maxed ? 5 : 0)})");
        return server.Scalar($"SELECT MAX(rowid_alias) FROM fellow WHERE user_no={user}") + 7000;
    }

    private Task<JsonObject> CraftAsync(long user, long recipe, long first, long second) =>
        server.CallGatewayAsync(1101, new { recipe_id = recipe, main_material_01 = first, main_material_02 = second, main_material_03 = 0, group_num = -1 }, user);

    private async Task<long> PlayerAsync(string name)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = name }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        server.Execute($"UPDATE gameuser SET gold={Gold} WHERE user_no={user}");
        foreach (long item in Materials)
            server.Execute($"INSERT INTO user_goods(user_no,item_id,count) VALUES({user},{item},1000)");
        return user;
    }

    private static long Errno(JsonObject reply) => reply["errno"]!.GetValue<long>();
}
