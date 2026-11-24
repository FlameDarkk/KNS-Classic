using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public class HeroPointShopTests(ServerFixture server)
{
    [Theory]
    [InlineData(9720104, 110003, 1500)]
    [InlineData(9720105, 110004, 5000)]
    public async Task Angel_purchase_spends_table_price_and_delivers_parser_shaped_persisted_ally(long product, long angel, long price)
    {
        long user = await PlayerAsync($"hp-angel-{product}", price + 17);
        long count = server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user}");
        long gold = server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");
        long gems = server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}");
        JsonObject reply = await server.CallGatewayAsync(903, new { product_id = product }, user);
        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        Assert.False(reply.ContainsKey("buff_etc_info"));
        Assert.IsType<JsonObject>(reply["new_fellow_info"]);
        Assert.IsType<JsonObject>(reply["fellow_book_info"]);
        Assert.Equal(angel, reply["new_fellow_info"]!["fellow_id"]!.GetValue<long>());
        Assert.Equal(angel, reply["fellow_book_info"]!["fellow_id"]!.GetValue<long>());
        Assert.Equal(17, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900016"));
        Assert.Equal(count + 1, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user}"));
        Assert.Equal(gold, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.Equal(gems, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        JsonObject loaded = await server.CallGatewayAsync(60, new { }, user);
        Assert.Contains(loaded["fellow_info"]!.AsArray(), row => row!["fellow_id"]!.GetValue<long>() == angel);
        Assert.NotEqual(0, (await server.CallGatewayAsync(903, new { product_id = product }, user))["errno"]!.GetValue<long>());
        Assert.Equal(count + 1, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user}"));
    }

    [Theory]
    [InlineData(9720111, 1901001, 4000)]
    [InlineData(9720117, 1901007, 500)]
    public async Task Collectible_purchase_credits_ten_goods_with_a_goods_only_reply(long product, long item, long price)
    {
        long user = await PlayerAsync($"hp-goods-{product}", price + 3);
        long fellows = server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user}");
        JsonObject reply = await server.CallGatewayAsync(903, new { product_id = product }, user);
        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        // No new_fellow_info / buff_etc_info: the parser's goods-only mode (UpdateUserGoods).
        Assert.False(reply.ContainsKey("new_fellow_info"));
        Assert.False(reply.ContainsKey("fellow_book_info"));
        Assert.False(reply.ContainsKey("buff_etc_info"));
        Assert.Contains(reply["update_item_etc_info"]!.AsArray(), row => row!["item_id"]!.GetValue<long>() == item && row["count"]!.GetValue<long>() == 10);
        Assert.Equal(3, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900016"));
        Assert.Equal(10, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id={item}"));
        Assert.Equal(fellows, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user}"));
    }

    [Fact]
    public async Task Auto_skill_purchase_unlocks_once_and_persists_through_login()
    {
        long user = await PlayerAsync("hp-auto-skill", 12000);
        Assert.Empty((await server.CallGatewayAsync(60, new { }, user))["buff_etc_info"]!.AsArray());
        JsonObject reply = await server.CallGatewayAsync(903, new { product_id = 9720118 }, user);
        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        Assert.False(reply.ContainsKey("new_fellow_info"));
        Assert.Contains(reply["buff_etc_info"]!.AsArray(), row => row!["type"]!.GetValue<long>() == 5);
        Assert.Equal(7000, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900016"));
        JsonObject loaded = await server.CallGatewayAsync(60, new { }, user);
        Assert.Contains(loaded["buff_etc_info"]!.AsArray(), row => row!["type"]!.GetValue<long>() == 5);
        Assert.NotEqual(0, (await server.CallGatewayAsync(903, new { product_id = 9720118 }, user))["errno"]!.GetValue<long>());
        Assert.Equal(7000, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900016"));
    }

    [Fact]
    public async Task Max_stamina_upgrade_adds_two_per_purchase_up_to_ten_purchases()
    {
        long user = await PlayerAsync("hp-max-stamina", 2500 * 11);
        for (int i = 1; i <= 10; i++)
        {
            JsonObject reply = await server.CallGatewayAsync(901, new { }, user);
            Assert.Equal(0, reply["errno"]!.GetValue<long>());
            Assert.Equal(i, reply["user_info"]!["user_max_stamina_buff"]!.GetValue<long>());
            Assert.Equal(32, reply["user_info"]!["user_max_stamina"]!.GetValue<long>());
        }

        Assert.NotEqual(0, (await server.CallGatewayAsync(901, new { }, user))["errno"]!.GetValue<long>());
        Assert.Equal(2500, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900016"));
        // Level 10 gives 32 base stamina (master_level.max_stamina); ten upgrades add 20.
        server.Execute($"INSERT OR REPLACE INTO player_stamina(user_no,stamina,last_gain) VALUES({user},0,1)");
        Assert.Equal(52, (await server.CallGatewayAsync(60, new { }, user))["user_info"]!["user_stamina"]!.GetValue<long>());
    }

    [Fact]
    public async Task Recovery_upgrade_shortens_the_refill_period_by_ten_seconds_each()
    {
        long user = await PlayerAsync("hp-stamina-time", 5000);
        Assert.Equal(0, (await server.CallGatewayAsync(902, new { }, user))["errno"]!.GetValue<long>());
        JsonObject reply = await server.CallGatewayAsync(902, new { }, user);
        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(2, reply["user_info"]!["user_stamina_time_buff"]!.GetValue<long>());
        Assert.Equal(0, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900016"));
        Assert.NotEqual(0, (await server.CallGatewayAsync(902, new { }, user))["errno"]!.GetValue<long>());
        // 280 seconds per point now: an empty bar refilled 2800 seconds ago holds 10.
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        server.Execute($"INSERT OR REPLACE INTO player_stamina(user_no,stamina,last_gain) VALUES({user},0,{now - 2800 - 5})");
        Assert.Equal(10, (await server.CallGatewayAsync(60, new { }, user))["user_info"]!["user_stamina"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(9720118, 4999)]
    [InlineData(9720110, 10000)]
    [InlineData(9720107, 10000)]
    [InlineData(9720104, 1499)]
    [InlineData(9720105, 4999)]
    [InlineData(9720101, 10000)]
    [InlineData(9720109, 10000)]
    [InlineData(-1, 10000)]
    public async Task Refused_purchase_preserves_points_and_allies(long product, long points)
    {
        long user = await PlayerAsync($"hp-refused-{product}", points);
        long count = server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user}");
        Assert.NotEqual(0, (await server.CallGatewayAsync(903, new { product_id = product }, user))["errno"]!.GetValue<long>());
        Assert.Equal(points, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900016"));
        Assert.Equal(count, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user}"));
    }

    private async Task<long> PlayerAsync(string device, long points)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        server.Execute($"INSERT OR REPLACE INTO user_goods(user_no,item_id,count) VALUES({user},1900016,{points})");
        return user;
    }
}
