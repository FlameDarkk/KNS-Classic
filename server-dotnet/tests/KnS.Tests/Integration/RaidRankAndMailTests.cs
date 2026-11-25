using KnS.Data.Repositories;
using KnS.Game.Protocols.Raid;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public sealed class RaidRankAndMailTests(ServerFixture server)
{
    [Fact]
    public async Task Closed_raid_rewards_freeze_standings_and_arrive_through_mail_once()
    {
        long user = await PlayerAsync("raid-rank-mail");
        long other = await PlayerAsync("raid-rank-mail-other");
        server.Execute($"INSERT INTO raid_score(raid_no,user_no,score,plays) VALUES(987654,{user},1000,1),(987654,{other},500,1)");
        JsonObject board = await server.CallGatewayAsync(501, new { }, user);
        Assert.Equal(987654, board["reward_raid_info"]!["raid_no"]!.GetValue<long>());
        Assert.Equal(0, board["reward_raid_info"]!["take_reward"]!.GetValue<long>());
        JsonObject reward = await server.CallGatewayAsync(506, new { raid_no = 987654 }, user);
        Assert.Equal(0, Error(reward));
        Assert.Equal(1, reward["reward_raid_info"]!["Grade"]!.GetValue<long>());
        Assert.Equal(2000, reward["reward_raid_info"]!["Reward1_Value"]!.GetValue<long>());
        Assert.Equal(5, reward["reward_raid_info"]!["Reward2_Value"]!.GetValue<long>());
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(506, new { raid_no = 987654 }, user)));
        server.Execute($"UPDATE raid_score SET score=10000 WHERE raid_no=987654 AND user_no={other}");
        JsonObject secondReward = await server.CallGatewayAsync(506, new { raid_no = 987654 }, other);
        Assert.Equal(2, secondReward["reward_raid_info"]!["Grade"]!.GetValue<long>());
        Assert.Equal(1900, secondReward["reward_raid_info"]!["Reward1_Value"]!.GetValue<long>());
        Assert.Equal(4, secondReward["reward_raid_info"]!["Reward2_Value"]!.GetValue<long>());
        JsonObject inbox = await server.CallGatewayAsync(63, new { type = 2, page = 1, count = 30 }, user);
        Assert.Single(inbox["mail_info"]!.AsArray());
        long mail = inbox["mail_info"]![0]!["mail_no"]!.GetValue<long>();
        Assert.Equal(2000, inbox["mail_info"]![0]!["value1"]!.GetValue<long>());
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(115, new { mail_list = new[] { mail } }, other)));
        long points = server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900016");
        Assert.Equal(0, Error(await server.CallGatewayAsync(115, new { mail_list = new[] { mail } }, user)));
        Assert.Equal(points + 2000, server.Scalar($"SELECT count FROM user_goods WHERE user_no={user} AND item_id=1900016"));
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(115, new { mail_list = new[] { mail } }, user)));
        Assert.Empty((await server.CallGatewayAsync(63, new { type = 2 }, user))["mail_info"]!.AsArray());
        long current = board["ongoing_raid_info"]![0]!["raid_no"]!.GetValue<long>();
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(506, new { raid_no = current }, user)));
    }

    [Fact]
    public async Task Five_star_ticket_mail_grants_one_persisted_five_star_ally_once()
    {
        long user = await PlayerAsync("mail-five-star-draw");
        using var scope = server.CreateScope();
        MailRepository mail = scope.ServiceProvider.GetRequiredService<MailRepository>();
        long number = mail.Add(user, 1900019, 1);
        JsonObject reply = await server.CallGatewayAsync(115, new { mail_list = new[] { number } }, user);
        Assert.Equal(0, Error(reply));
        JsonNode ally = Assert.Single(reply["fellow_info"]!.AsArray())!;
        long id = ally["fellow_id"]!.GetValue<long>();
        Assert.Equal(5, server.Scalar($"SELECT grade FROM master_fellow WHERE fellow_id={id}"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id={id}"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow_book WHERE user_no={user} AND fellow_id={id}"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow_draw_audit WHERE user_no={user} AND source='mail_ticket' AND payment_item=1900019 AND payment_count=1 AND fellow_id={id} AND grade=5"));
        Assert.Equal(0, server.Scalar($"SELECT COALESCE(SUM(count),0) FROM user_goods WHERE user_no={user} AND item_id=1900019"));
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(115, new { mail_list = new[] { number } }, user)));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id={id}"));
        JsonObject login = await server.CallGatewayAsync(60, new { }, user);
        Assert.Contains(login["fellow_info"]!.AsArray(), row => row!["fellow_id"]!.GetValue<long>() == id);
    }

    [Theory]
    [InlineData(1900018, 4)]
    [InlineData(1900020, 6)]
    public async Task Four_and_six_star_ticket_mails_grant_matching_drawable_allies_and_audit(long ticketItem, long expectedGrade)
    {
        long user = await PlayerAsync($"mail-star-ticket-{expectedGrade}");
        using var scope = server.CreateScope();
        MailRepository mail = scope.ServiceProvider.GetRequiredService<MailRepository>();
        long number = mail.Add(user, ticketItem, 1);
        JsonObject reply = await server.CallGatewayAsync(115, new { mail_list = new[] { number } }, user);
        Assert.Equal(0, Error(reply));
        JsonNode ally = Assert.Single(reply["fellow_info"]!.AsArray())!;
        long id = ally["fellow_id"]!.GetValue<long>();
        Assert.Equal(expectedGrade, server.Scalar($"SELECT grade FROM master_fellow WHERE fellow_id={id}"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow WHERE user_no={user} AND fellow_id={id}"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow_book WHERE user_no={user} AND fellow_id={id}"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM fellow_draw_audit WHERE user_no={user} AND source='mail_ticket' AND payment_item={ticketItem} AND payment_count=1 AND fellow_id={id} AND grade={expectedGrade}"));
        Assert.Equal(0, server.Scalar($"SELECT COALESCE(SUM(count),0) FROM user_goods WHERE user_no={user} AND item_id={ticketItem}"));
    }

    [Fact]
    public async Task Mail_validates_whole_batch_before_delivering_currency_equipment_and_multiple_allies()
    {
        long user = await PlayerAsync("mail-batch");
        using var scope = server.CreateScope();
        MailRepository mail = scope.ServiceProvider.GetRequiredService<MailRepository>();
        long valid = mail.Add(user, 110004, 2, 1900008, 55, message: "Reward");
        long invalid = mail.Add(user, long.MaxValue, 1);
        long gold = server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(115, new { mail_list = new[] { valid, invalid } }, user)));
        Assert.Equal(2, mail.Count(user));
        Assert.Equal(gold, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(115, new { mail_list = new[] { valid, valid } }, user)));
        JsonObject delivered = await server.CallGatewayAsync(115, new { mail_list = new[] { valid } }, user);
        Assert.Equal(0, Error(delivered));
        Assert.Equal(2, delivered["fellow_info"]!.AsArray().Count);
        Assert.Equal(gold + 55, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
        long itemId = server.Scalar("SELECT MIN(item_id) FROM master_item");
        long equipment = mail.Add(user, itemId, 1);
        JsonObject item = await server.CallGatewayAsync(115, new { mail_list = new[] { equipment } }, user);
        Assert.Equal(0, Error(item));
        Assert.Single(item["item_info"]!.AsArray());
        Assert.NotEqual(0, Error(await server.CallGatewayAsync(63, new { type = 2, page = long.MaxValue }, user)));
    }

    [Theory]
    [InlineData(1, 1000, 1)]
    [InlineData(10, 1000, 4)]
    [InlineData(100, 1000, 8)]
    [InlineData(101, 10000, 9)]
    [InlineData(110, 1000, 12)]
    [InlineData(210, 1000, 13)]
    [InlineData(1000, 1000, 20)]
    public void Rank_tiers_follow_displayed_absolute_and_percentage_boundaries(long rank, long total, long grade)
    {
        using var scope = server.CreateScope();
        Assert.Equal(grade, RaidRankRewardRules.Select(scope.ServiceProvider.GetRequiredService<RaidRepository>().RankRewards(), rank, total)!.Grade);
    }

    [Fact]
    public void Raid_rank_ticket_rewards_follow_native_tiers()
    {
        using var scope = server.CreateScope();
        IReadOnlyList<RaidRankRewardRule> rules = scope.ServiceProvider.GetRequiredService<RaidRepository>().RankRewards();
        Assert.Equal(2000, rules.Single(r => r.Grade == 1).Reward1Value);
        Assert.Equal(1900, rules.Single(r => r.Grade == 2).Reward1Value);
        long[] expectedTickets = [5, 4, 4, 3, 3, 2, 2, 2, 2, 1, 1, 1];
        for (int grade = 1; grade <= expectedTickets.Length; grade++)
        {
            RaidRankRewardRule row = rules.Single(r => r.Grade == grade);
            Assert.Equal(1900012, row.Reward2Item);
            Assert.Equal(expectedTickets[grade - 1], row.Reward2Value);
        }
    }

    private async Task<long> PlayerAsync(string name)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = name }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        return user;
    }

    private static long Error(JsonObject reply) => reply["errno"]!.GetValue<long>();
}
