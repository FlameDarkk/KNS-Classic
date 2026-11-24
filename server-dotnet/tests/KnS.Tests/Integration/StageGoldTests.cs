using System.Text.Json.Nodes;
using KnS.Game.Rules;

namespace KnS.Tests.Integration;

/// <summary>
/// Protocol 72 pays the gold the client reports, capped at the most the gold piles can hold (DropRules.MaxStageGold).
/// </summary>
[Collection(SharedServer.Name)]
public class StageGoldTests(ServerFixture server)
{
    private const long Stage = 3000001;

    [Fact]
    public async Task Reported_gold_under_the_cap_is_paid_in_full()
    {
        long user = await StartAsync("stage-gold-under");
        long before = Gold(user);
        JsonObject result = await ResultAsync(user, reportedGold: 500);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Equal(before + 500, Gold(user));
    }

    [Fact]
    public async Task Reported_gold_over_the_cap_is_paid_at_the_cap()
    {
        long user = await StartAsync("stage-gold-over");
        long before = Gold(user);
        JsonObject result = await ResultAsync(user, reportedGold: 999999);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Equal(before + DropRules.MaxStageGold, Gold(user));
    }

    [Fact]
    public async Task Negative_gold_pays_nothing()
    {
        long user = await StartAsync("stage-gold-negative");
        long before = Gold(user);
        JsonObject result = await ResultAsync(user, reportedGold: -5);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Equal(before, Gold(user));
    }

    [Fact]
    public async Task Garbage_gold_pays_nothing()
    {
        long user = await StartAsync("stage-gold-garbage");
        long before = Gold(user);
        JsonObject result = await server.CallGatewayAsync(72, new
        {
            stage_no = Stage, stage_mode = 1, stage_result = 1, reward_gold = "abc",
            reward_items = Array.Empty<long>(), event_items = Array.Empty<long>(),
        }, user);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Equal(before, Gold(user));
    }

    [Fact]
    public async Task Failed_stage_still_pays_its_capped_gold()
    {
        long user = await StartAsync("stage-gold-failed");
        server.Execute($"UPDATE stage_start SET start_time=start_time-60 WHERE user_no={user}");
        long before = Gold(user);
        JsonObject result = await ResultAsync(user, reportedGold: 300, stageResult: 0);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Equal(before + 300, Gold(user));
    }

    [Fact]
    public async Task Result_without_a_start_pays_no_gold_and_a_start_pays_once()
    {
        long user = await StartAsync("stage-gold-once");
        long before = Gold(user);
        await ResultAsync(user, reportedGold: 400);
        JsonObject replay = await ResultAsync(user, reportedGold: 400);
        Assert.Equal(0, replay["errno"]!.GetValue<long>());
        Assert.Equal(before + 400, Gold(user));
    }

    [Fact]
    public async Task Dying_in_the_first_15_seconds_costs_one_run()
    {
        long user = await StartAsync("stage-gold-misclick");
        long goldBefore = Gold(user);
        long staminaAfterStart = Stamina(user);
        long cost = server.Scalar($"SELECT stamina_cost FROM stage_start WHERE user_no={user}");
        JsonObject result = await ResultAsync(user, reportedGold: 0, stageResult: 0);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Equal(goldBefore, Gold(user));
        Assert.True(cost > 0);
        Assert.Equal(staminaAfterStart, Stamina(user));
    }

    [Fact]
    public async Task Clearing_within_15_seconds_still_pays()
    {
        long user = await StartAsync("stage-gold-fast-clear");
        long before = Gold(user);
        await ResultAsync(user, reportedGold: 200);
        Assert.Equal(before + 200, Gold(user));
    }

    private long Stamina(long user) => server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}");

    private async Task<long> StartAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        JsonObject start = await server.CallGatewayAsync(71, new { friend_type = 0, stage_id = Stage, stage_mode = 1, friend_user_no = 0 }, user);
        Assert.Equal(0, start["errno"]!.GetValue<long>());
        // Only the reported gold: a cleared stage also pays its unpicked Gold Boxes and items.
        server.Execute($"DELETE FROM stage_event_offer WHERE user_no={user}");
        server.Execute($"DELETE FROM item WHERE user_no={user} AND claimed=0");
        return user;
    }

    private Task<JsonObject> ResultAsync(long user, object reportedGold, int stageResult = 1) =>
        server.CallGatewayAsync(72, new
        {
            stage_no = Stage, stage_mode = 1, stage_result = stageResult, reward_gold = reportedGold,
            reward_items = Array.Empty<long>(), event_items = Array.Empty<long>(),
        }, user);

    private long Gold(long user) => server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");
}
