using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public class GemPurchaseTests(ServerFixture server)
{
    private async Task<long> PlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        server.Execute($"UPDATE gameuser SET gold=1000,cash=100 WHERE user_no={user}");
        return user;
    }

    [Theory]
    [InlineData(9001001, 10, 15000)]
    [InlineData(9001002, 50, 82500)]
    [InlineData(9001003, 100, 180000)]
    [InlineData(9001004, 200, 390000)]
    [InlineData(9001005, 300, 630000)]
    [InlineData(9001006, 600, 1350000)]
    public async Task Gold_bundle_spends_gems_and_persists_base_and_bonus_gold(long product, long price, long gold)
    {
        long user = await PlayerAsync($"gem-gold-purchase-{product}");
        server.Execute($"UPDATE gameuser SET cash=1000 WHERE user_no={user}");
        JsonObject reply = await server.CallGatewayAsync(109, new { product_id = product }, user);
        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(1000 + gold, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.Equal(1000 - price, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Contains(reply["update_item_etc_info"]!.AsArray(), item => item!["item_id"]!.GetValue<long>() == 1900008 && item["count"]!.GetValue<long>() == 1000 + gold);
        JsonObject load = await server.CallGatewayAsync(60, new { user_no = user }, user);
        Assert.Equal(1000 + gold, load["user_info"]!["user_gold"]!.GetValue<long>());
    }

    [Fact]
    public async Task Unknown_or_unaffordable_gold_bundle_preserves_balance()
    {
        long user = await PlayerAsync("gem-gold-refusal");
        foreach (long product in new long[] { 9001006, -1 })
            Assert.NotEqual(0, (await server.CallGatewayAsync(109, new { product_id = product }, user))["errno"]!.GetValue<long>());
        Assert.Equal(1000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.Equal(100, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task Refill_restores_stamina_to_max_and_refuses_when_full()
    {
        long user = await PlayerAsync("gem-stamina-refill");
        server.Execute($"INSERT INTO player_stamina(user_no,stamina,last_gain) VALUES({user},20,{DateTimeOffset.UtcNow.ToUnixTimeSeconds()})");
        JsonObject reply = await server.CallGatewayAsync(105, new { }, user);
        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(109, server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}"));
        Assert.Equal(95, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(109, reply["user_info"]!["user_stamina"]!.GetValue<long>());
        JsonObject reload = await server.CallGatewayAsync(60, new { user_no = user }, user);
        Assert.Equal(109, reload["user_info"]!["user_stamina"]!.GetValue<long>());
        Assert.NotEqual(0, (await server.CallGatewayAsync(105, new { }, user))["errno"]!.GetValue<long>());
        Assert.Equal(95, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
    }

    [Theory]
    [InlineData(9730010, 1600011, 16)]
    [InlineData(9730011, 1600012, 30)]
    [InlineData(9730012, 1600013, 56)]
    public async Task Gem_book_purchase_delivers_an_inventory_instance(long product, long item, long price)
    {
        long user = await PlayerAsync($"gem-book-{product}");
        JsonObject reply = await server.CallGatewayAsync(904, new { product_id = product }, user);
        Assert.Equal(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(item, reply["item_info"]!["item_id"]!.GetValue<long>());
        Assert.Equal(100 - price, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user} AND item_id={item} AND claimed=1"));
        JsonObject inventory = await server.CallGatewayAsync(61, new { }, user);
        Assert.Contains(inventory["item_infos"]!.AsArray(), row => row!["item_id"]!.GetValue<long>() == item);
    }

    [Fact]
    public async Task Gem_book_purchase_refuses_without_funds()
    {
        long user = await PlayerAsync("gem-book-insufficient");
        server.Execute($"UPDATE gameuser SET cash=0 WHERE user_no={user}");
        JsonObject reply = await server.CallGatewayAsync(904, new { product_id = 9730010 }, user);
        Assert.NotEqual(0, reply["errno"]!.GetValue<long>());
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user} AND item_id=1600011"));
    }
}

