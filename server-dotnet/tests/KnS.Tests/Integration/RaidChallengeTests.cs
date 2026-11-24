using System.Text.Json.Nodes;
using KnS.Game.Protocols.Raid;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public sealed class RaidChallengeTests(ServerFixture server)
{
    [Fact]
    public async Task Settled_damage_unlocks_matching_boss_only_and_claims_gold_once()
    {
        long user = await PlayerAsync("raid-challenge-score");
        long other = await PlayerAsync("raid-challenge-other");
        long raid = (await server.CallGatewayAsync(501, new { }, user))["ongoing_raid_info"]![0]!["raid_no"]!.GetValue<long>();
        long spider = Enumerable.Range(0, RaidRules.Bosses.Count).Select(offset => raid + offset).First(number => RaidRules.BossOf(number).StageId == 3100011);
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(511, new { challenge_id = 5300028 }, user)));
        await server.CallGatewayAsync(503, new { raid_no = raid }, user);
        server.Execute($"UPDATE raid_play SET raid_no={spider} WHERE user_no={user}");
        JsonObject settled = await server.CallGatewayAsync(504, new { score = 10000 }, user);
        Assert.Contains(settled["raid_challenge_info"]!.AsArray(), row => row!["raid_id"]!.GetValue<long>() == 3100011
            && row["count"]!.GetValue<long>() == 10000);
        await server.CallGatewayAsync(504, new { score = 20000000 }, user);
        Assert.Equal(10000, server.Scalar($"SELECT score FROM raid_challenge_progress WHERE user_no={user} AND raid_id=3100011"));
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(511, new { challenge_id = 5300027 }, user)));
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(511, new { challenge_id = 5300128 }, user)));
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(511, new { challenge_id = 5300028 }, other)));
        long gold = server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");
        JsonObject claim = await server.CallGatewayAsync(511, new { challenge_id = 5300028 }, user);
        Assert.Equal(0, Error(claim));
        Assert.Equal(5300028, claim["raid_challenge_reward_info"]!["challenge_id"]!.GetValue<long>());
        Assert.IsType<JsonObject>(claim["fellow_info"]);
        Assert.IsType<JsonObject>(claim["fellow_book_info"]);
        Assert.IsType<JsonObject>(claim["item_info"]);
        Assert.Equal(gold + 1000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(511, new { challenge_id = 5300028 }, user)));
        Assert.Equal(gold + 1000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.Single((await server.CallGatewayAsync(508, new { }, user))["raid_challenge_reward_info"]!.AsArray());
        Assert.Empty((await server.CallGatewayAsync(508, new { }, other))["raid_challenge_reward_info"]!.AsArray());
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(511, new { challenge_id = long.MaxValue }, user)));
    }

    [Fact]
    public async Task All_196_browser_challenges_deliver_exact_currency_and_ally_quantities_once()
    {
        long user = await PlayerAsync("raid-challenge-all");
        server.Execute($"UPDATE gameuser SET gold=0,cash=0 WHERE user_no={user}");
        server.Execute($"DELETE FROM user_goods WHERE user_no={user}");
        server.Execute($"INSERT INTO raid_challenge_progress(user_no,raid_id,score) SELECT {user},raid_id,20000000 FROM master_raid_challenge GROUP BY raid_id");
        long alliesBefore = server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id=110004");
        int claims = 0;
        for (long group = 0; group < 7; group++)
        {
            for (long tier = 1; tier <= 28; tier++)
            {
                long id = 5300000 + group * 100 + tier;
                JsonObject reply = await server.CallGatewayAsync(511, new { challenge_id = id }, user);
                Assert.Equal(0, Error(reply));
                claims++;
                if (tier == 3)
                {
                    Assert.Equal(110004, reply["fellow_info"]!["fellow_id"]!.GetValue<long>());
                    Assert.Single(reply["granted_fellow_infos"]!.AsArray());
                }
                Assert.NotEqual(0, Error(await server.CallGatewayAsync(511, new { challenge_id = id }, user)));
            }
        }
        Assert.Equal(196, claims);
        Assert.Equal(1393000, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.Equal(854, server.Scalar($"SELECT cash FROM gameuser WHERE user_no={user}"));
        Assert.Equal(7700, Goods(1900016));
        Assert.Equal(634, server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}"));
        Assert.Equal(42, Goods(1900012));
        Assert.Equal(alliesBefore + 14, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id=110004"));
        JsonObject inbox = await server.CallGatewayAsync(63, new { type = 2, page = 1, count = 30 }, user);
        long[] pending = inbox["mail_info"]!.AsArray().Select(row => row!["mail_no"]!.GetValue<long>()).ToArray();
        Assert.Equal(7, pending.Length);
        JsonObject delivered = await server.CallGatewayAsync(115, new { mail_list = pending }, user);
        Assert.Equal(0, Error(delivered));
        Assert.Equal(7, delivered["fellow_info"]!.AsArray().Count);
        Assert.Equal(alliesBefore + 21, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id=110004"));
        Assert.Equal(196, (await server.CallGatewayAsync(508, new { }, user))["raid_challenge_reward_info"]!.AsArray().Count);
        long Goods(long item) => server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id={item}");
    }

    [Fact]
    public async Task Historical_scores_restore_best_boss_progress_without_resetting_claims()
    {
        long user = await PlayerAsync("raid-challenge-history");
        server.Execute($"INSERT INTO raid_score(raid_no,user_no,score,plays) VALUES(0,{user},20000,1),(9000,{user},10000,1)");
        JsonObject reply = await server.CallGatewayAsync(510, new { }, user);
        Assert.Contains(reply["raid_challenge_info"]!.AsArray(), row => row!["raid_id"]!.GetValue<long>() == 3100011
            && row["count"]!.GetValue<long>() == 20000);
        Assert.Equal(0, Error(await server.CallGatewayAsync(511, new { challenge_id = 5300028 }, user)));
        await server.CallGatewayAsync(510, new { }, user);
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(511, new { challenge_id = 5300028 }, user)));
    }

    private async Task<long> PlayerAsync(string name)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = name }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        return user;
    }

    private static long Error(JsonObject reply) => reply["errno"]!.GetValue<long>();
}
