using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

/// <summary>Steel Anvil: 331 craft, 335 power-up, 336 disassemble (ally gear).</summary>
[Collection(SharedServer.Name)]
public sealed class FellowItemAnvilTests(ServerFixture server)
{
    private const long Offset = 5_000_000;
    private const long Dagger = 1200001;   // Rusty Dagger: need 115, growth 30 %, feeds 25, c40 200
    private const long Knight = 1200002;   // Knight's Dagger: crafted from a Rusty Dagger + 10/10 subs + 1000 G

    [Fact]
    public async Task Power_up_spends_fed_gear_and_materials_and_levels_the_target()
    {
        long user = await PlayerAsync("anvil-powerup");
        long target = AddItem(user, Dagger);
        long fed = AddItem(user, Dagger);
        JsonObject reply = await server.CallGatewayAsync(335, new
        {
            fellow_item_no = target,
            used_fellow_item_no_list = new[] { fed },
            used_currency_list = new[] { new { id = 1902001, count = 10 } },
        }, user);
        Assert.Equal(0, Errno(reply));
        // 25 (fed dagger) + 10 x 10 (materials) = 125 >= 115: level 1 with 10 left; need for level 2 = 115 + 34 = 149.
        JsonObject item = reply["fellow_item_info"]!.AsObject();
        Assert.Equal(1, item["fellow_item_upgrade"]!.GetValue<long>());
        Assert.Equal(10, item["fellow_item_upgrade_exp"]!.GetValue<long>());
        Assert.Equal(149, item["fellow_item_upgrade_need_exp"]!.GetValue<long>());
        Assert.Equal([fed], reply["used_fellow_item_no_list"]!.AsArray().Select(n => n!.GetValue<long>()));
        Assert.Equal(1_000_000 - 200 - 500, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.Equal(990, Count(user, 1902001));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow_item WHERE rowid_alias={fed - Offset}"));
    }

    [Fact]
    public async Task Power_up_refuses_worn_or_locked_feed_and_keeps_everything()
    {
        long user = await PlayerAsync("anvil-powerup-refuse");
        long target = AddItem(user, Dagger);
        long locked = AddItem(user, Dagger);
        server.Execute($"UPDATE fellow_item SET locked=1 WHERE rowid_alias={locked - Offset}");
        JsonObject reply = await server.CallGatewayAsync(335, new
        {
            fellow_item_no = target, used_fellow_item_no_list = new[] { locked }, used_currency_list = Array.Empty<object>(),
        }, user);
        Assert.NotEqual(0, Errno(reply));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow_item WHERE rowid_alias={locked - Offset}"));
        Assert.Equal(1_000_000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task Disassemble_returns_sub_materials_in_the_goods_list()
    {
        long user = await PlayerAsync("anvil-disassemble");
        long item = AddItem(user, Knight);
        JsonObject reply = await server.CallGatewayAsync(336, new { fellow_item_no_list = new[] { item } }, user);
        Assert.Equal(0, Errno(reply));
        Assert.Equal([item], reply["decomposition_fellow_item_no_list"]!.AsArray().Select(n => n!.GetValue<long>()));
        Assert.True(Count(user, 1902001) > 1000 && Count(user, 1902002) > 1000);
        JsonArray goods = reply["update_item_etc_info"]!.AsArray();
        Assert.Contains(goods, row => row!["item_id"]!.GetValue<long>() == 1902001 && row["count"]!.GetValue<long>() == Count(user, 1902001));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow_item WHERE rowid_alias={item - Offset}"));
    }

    [Fact]
    public async Task Craft_consumes_the_main_gear_materials_and_gold()
    {
        long user = await PlayerAsync("anvil-craft");
        long main = AddItem(user, Dagger);
        JsonObject reply = await server.CallGatewayAsync(331, new
        {
            recipe_id = Knight,
            main_material_list = new[] { new { main_material_01 = main, main_material_02 = 0, main_material_03 = 0 } },
        }, user);
        Assert.Equal(0, Errno(reply));
        Assert.Equal(Knight, reply["fellow_item_infos"]!.AsArray()[0]!["fellow_item_id"]!.GetValue<long>());
        Assert.Equal(1, reply["make_count"]!.GetValue<long>());
        Assert.Equal(990, Count(user, 1902001));
        Assert.Equal(1_000_000 - 1000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM fellow_item WHERE rowid_alias={main - Offset}"));
    }

    [Fact]
    public async Task Craft_takes_every_copy_the_recipe_needs()
    {
        // 1200004 needs main_value1 = 2 copies of 1200003 (+17/17 subs, 3000 G).
        long user = await PlayerAsync("anvil-craft-copies");
        long first = AddItem(user, 1200003);
        long second = AddItem(user, 1200003);
        JsonObject reply = await server.CallGatewayAsync(331, new
        {
            recipe_id = 1200004,
            main_material_list = new[] { new { main_material_01 = first, main_material_02 = second, main_material_03 = 0 } },
        }, user);
        Assert.Equal(0, Errno(reply));
        Assert.Equal(1200004, reply["fellow_item_infos"]!.AsArray()[0]!["fellow_item_id"]!.GetValue<long>());
        Assert.Equal([first, second], reply["used_fellow_item_no_list"]!.AsArray().Select(n => n!.GetValue<long>()));
        Assert.Equal(1000 - 17, Count(user, 1902001));
        Assert.Equal(1_000_000 - 3000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));

        long only = AddItem(user, 1200003);
        JsonObject missing = await server.CallGatewayAsync(331, new
        {
            recipe_id = 1200004,
            main_material_list = new[] { new { main_material_01 = only, main_material_02 = 0, main_material_03 = 0 } },
        }, user);
        Assert.NotEqual(0, Errno(missing));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow_item WHERE rowid_alias={only - Offset}"));
    }

    private long AddItem(long user, long id)
    {
        server.Execute($"INSERT INTO fellow_item(user_no,fellow_item_id) VALUES({user},{id})");
        return server.Scalar($"SELECT MAX(rowid_alias) FROM fellow_item WHERE user_no={user}") + Offset;
    }

    private long Count(long user, long item) => server.Scalar($"SELECT COALESCE(MAX(count),0) FROM user_goods WHERE user_no={user} AND item_id={item}");

    private async Task<long> PlayerAsync(string name)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = name }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        server.Execute($"UPDATE gameuser SET gold=1000000 WHERE user_no={user}");
        for (long item = 1902001; item <= 1902008; item++)
            server.Execute($"INSERT INTO user_goods(user_no,item_id,count) VALUES({user},{item},1000)");
        return user;
    }

    private static long Errno(JsonObject reply) => reply["errno"]!.GetValue<long>();
}
