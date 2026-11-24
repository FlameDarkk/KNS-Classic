using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public class FriendshipShopTests(ServerFixture server)
{
    [Fact]
    public async Task Stamina_product_spends_points_and_raises_the_stamina_bar()
    {
        long user = await PlayerAsync("friendship-stamina", 150);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        server.Execute($"INSERT OR REPLACE INTO player_stamina(user_no,stamina,last_gain) VALUES({user},0,{now})");

        JsonObject reply = await server.CallGatewayAsync(904, new { product_id = 9730003 }, user);

        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        Assert.False(reply.ContainsKey("item_info"));
        Assert.Equal(50, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900011"));
        Assert.Equal(10, server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}"));
        Assert.Contains(reply["update_item_etc_info"]!.AsArray(), row => row!["item_id"]!.GetValue<long>() == 1900014 && row["count"]!.GetValue<long>() == 10);
        JsonObject loaded = await server.CallGatewayAsync(60, new { }, user);
        Assert.Equal(10, loaded["user_info"]!["user_stamina"]!.GetValue<long>());
    }

    [Fact]
    public async Task Short_friendship_point_balance_is_refused_and_changes_nothing()
    {
        long user = await PlayerAsync("friendship-short", 99);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        server.Execute($"INSERT OR REPLACE INTO player_stamina(user_no,stamina,last_gain) VALUES({user},0,{now})");

        JsonObject reply = await server.CallGatewayAsync(904, new { product_id = 9730003 }, user);

        Assert.NotEqual(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(99, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900011"));
        Assert.Equal(0, server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}"));
    }

    [Fact]
    public async Task Material_product_priced_in_points_lands_in_the_inventory()
    {
        // A test-only row: no shipped friendship-point product pays a material yet.
        server.Execute("INSERT OR REPLACE INTO master_friendship_shop(product_id,price_item_id,price,package_id,reward_item_id,reward_count) VALUES(9739001,1900011,20,0,1600011,1)");
        long user = await PlayerAsync("friendship-material", 25);

        JsonObject reply = await server.CallGatewayAsync(904, new { product_id = 9739001 }, user);

        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(1600011, reply["item_info"]!["item_id"]!.GetValue<long>());
        Assert.Equal(5, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900011"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user} AND item_id=1600011 AND claimed=1"));
        JsonObject inventory = await server.CallGatewayAsync(61, new { }, user);
        Assert.Contains(inventory["item_infos"]!.AsArray(), row => row!["item_id"]!.GetValue<long>() == 1600011);
    }

    private async Task<long> PlayerAsync(string device, long points)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        server.Execute($"INSERT OR REPLACE INTO user_goods(user_no,item_id,count) VALUES({user},1900011,{points})");
        return user;
    }
}
