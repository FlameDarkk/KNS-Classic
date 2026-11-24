using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public class AllyLevelingTests(ServerFixture server)
{
    private async Task<(long User, long Ally)> PlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        server.Execute($"INSERT INTO fellow(user_no, fellow_id) VALUES({user},100001)");
        long ally = server.Scalar($"SELECT MAX(rowid_alias) FROM fellow WHERE user_no={user}") + 7000;
        return (user, ally);
    }

    private long Potion(long user, long id = 1600001, bool locked = false, long claimed = 1)
    {
        server.Execute($"INSERT INTO item(user_no,item_id,claimed,locked) VALUES({user},{id},{claimed},{(locked ? 1 : 0)})");
        return server.Scalar($"SELECT MAX(rowid_alias) FROM item WHERE user_no={user}") + 2000;
    }

    private Task<JsonObject> LevelAsync(long user, long ally, params long[] potions) =>
        server.CallGatewayAsync(310, new { fellow_no = ally, use_item_no_list = potions }, user);

    [Fact]
    public async Task Ally_lock_persists_blocks_consumption_and_unlocks_explicitly()
    {
        var (user, ally) = await PlayerAsync("ally-lock");
        JsonObject locked = await server.CallGatewayAsync(305, new { fellow_no = ally, fellow_lock = 1 }, user);
        Assert.Equal(1, locked["fellow_info"]!["fellow_lock"]!.GetValue<long>());
        await server.CallGatewayAsync(305, new { fellow_no = ally, fellow_lock = 1 }, user);
        JsonObject sale = await server.CallGatewayAsync(306, new { sell_fellows = new[] { ally } }, user);
        Assert.Empty(sale["success_list"]!.AsArray());
        var (other, _) = await PlayerAsync("ally-lock-other");
        Assert.NotEqual(0, (await server.CallGatewayAsync(305, new { fellow_no = ally, fellow_lock = 0 }, other))["errno"]!.GetValue<long>());
        JsonObject loaded = await server.CallGatewayAsync(60, new { user_no = user }, user);
        Assert.Equal(1, loaded["fellow_info"]!.AsArray().Single(n => n!["fellow_no"]!.GetValue<long>() == ally)!["fellow_lock"]!.GetValue<long>());
        JsonObject unlocked = await server.CallGatewayAsync(305, new { fellow_no = ally, fellow_lock = 0 }, user);
        Assert.Equal(0, unlocked["fellow_info"]!["fellow_lock"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mission_allies_cannot_be_leveled_powered_up_or_consumed_by_enhancement(bool targetBusy)
    {
        var (user, target) = await PlayerAsync("xp-busy-" + targetBusy);
        server.Execute($"INSERT INTO fellow(user_no,fellow_id,level,upgrade) VALUES({user},100011,20,5)");
        long material = server.Scalar($"SELECT MAX(rowid_alias) FROM fellow WHERE user_no={user}") + 7000;
        server.Execute($"UPDATE fellow SET level=20,upgrade=5 WHERE rowid_alias={target - 7000}");
        long busy = targetBusy ? target : material;
        server.Execute($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,end_time,success_rate,rewards) VALUES({user},1,1,'{busy}','',1,100,'')");
        long gold = server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");
        JsonObject power = await server.CallGatewayAsync(303, new { fellow_no = target, used_fellow_no = material }, user);
        JsonObject enhance = await server.CallGatewayAsync(307, new { used_fellows = new[] { target, material } }, user);
        Assert.NotEqual(0, power["errno"]!.GetValue<long>());
        Assert.NotEqual(0, enhance["errno"]!.GetValue<long>());
        server.Execute($"UPDATE fellow SET level=1 WHERE rowid_alias={busy - 7000}");
        long potion = Potion(user);
        Assert.NotEqual(0, (await LevelAsync(user, busy, potion))["errno"]!.GetValue<long>());
        Assert.Equal(2, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user}"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user}"));
        Assert.Equal(gold, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
    }

    [Fact]
    public async Task Xp_potions_level_the_ally_keep_remainder_consume_instances_and_persist()
    {
        var (user, ally) = await PlayerAsync("xp-potion-ok");
        long small = Potion(user), medium = Potion(user, 1600002);
        JsonObject reply = await LevelAsync(user, ally, small, medium);
        JsonNode info = Assert.IsType<JsonObject>(reply["fellow_info"]);
        long level = 1, remainder = 1400;
        while (level < 20)
        {
            long needed = server.Scalar($"SELECT exp_to_next FROM master_fellow_level WHERE grade=1 AND level={level}");
            if (remainder < needed) { break; }
            remainder -= needed;
            level++;
        }
        Assert.Equal(level, info["fellow_level"]!.GetValue<long>());
        Assert.Equal(remainder, info["fellow_exp"]!.GetValue<long>());
        Assert.Equal(new[] { small, medium }, reply["use_item_no_list"]!.AsArray().Select(n => n!.GetValue<long>()));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user}"));
        JsonObject loaded = await server.CallGatewayAsync(60, new { user_no = user }, user);
        JsonNode persisted = loaded["fellow_info"]!.AsArray().Single(n => n!["fellow_no"]!.GetValue<long>() == ally)!;
        Assert.Equal(level, persisted["fellow_level"]!.GetValue<long>());
        Assert.Equal(remainder, persisted["fellow_exp"]!.GetValue<long>());
        Assert.NotNull(reply["fellow_book_info"]);
    }

    [Fact]
    public async Task Xp_stops_at_max_level_and_refuses_more_potions_without_consuming_them()
    {
        var (user, ally) = await PlayerAsync("xp-potion-cap");
        server.Execute($"UPDATE fellow SET level=19, exp=0 WHERE rowid_alias={ally - 7000}");
        long potion = Potion(user, 1600003);
        JsonObject reply = await LevelAsync(user, ally, potion);
        Assert.Equal(20, reply["fellow_info"]!["fellow_level"]!.GetValue<long>());
        Assert.Equal(0, reply["fellow_info"]!["fellow_exp"]!.GetValue<long>());
        Assert.Equal(20, server.Scalar($"SELECT level FROM fellow_book WHERE user_no={user} AND fellow_id=100001"));
        long spare = Potion(user);
        Assert.NotEqual(0, (await LevelAsync(user, ally, spare))["errno"]!.GetValue<long>());
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user}"));
        server.Execute($"DELETE FROM fellow WHERE rowid_alias={ally - 7000}");
        JsonObject claim = await server.CallGatewayAsync(308, new { fellow_id = 100001 }, user);
        Assert.Equal(0, claim["errno"]!.GetValue<long>());
        Assert.Equal(1, claim["fellow_book_info"]!["recv_reward"]!.GetValue<long>());
    }

    [Fact]
    public async Task Invalid_potion_batches_are_atomic_and_never_spend_other_players_items()
    {
        var (user, ally) = await PlayerAsync("xp-potion-invalid");
        var (other, foreignAlly) = await PlayerAsync("xp-potion-other");
        long good = Potion(user), foreign = Potion(other), locked = Potion(user, locked: true);
        long wrong = Potion(user, 1600011), offer = Potion(user, claimed: 0);
        foreach (long[] batch in new[] { new[] { good, foreign }, new[] { good, good }, new[] { good, locked },
                     new[] { good, wrong }, new[] { good, offer }, Array.Empty<long>(), Enumerable.Repeat(good, 11).ToArray() })
        {
            Assert.NotEqual(0, (await LevelAsync(user, ally, batch))["errno"]!.GetValue<long>());
        }
        Assert.NotEqual(0, (await LevelAsync(user, foreignAlly, good))["errno"]!.GetValue<long>());
        Assert.Equal(4, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user}"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={other}"));
        Assert.Equal(1, server.Scalar($"SELECT level FROM fellow WHERE rowid_alias={ally - 7000}"));
    }
}
