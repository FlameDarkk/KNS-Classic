using System.Text.Json.Nodes;
using KnS.Data;
using KnS.Game.Players;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public class LevelStaminaTests(ServerFixture server)
{
    private async Task<long> PlayerAsync()
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = "level-stamina-" + Guid.NewGuid().ToString("N") }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        server.Execute($"UPDATE gameuser SET level=1,exp=0 WHERE user_no={user}");
        return user;
    }

    [Fact]
    public async Task Future_stamina_clock_is_reset_without_losing_stamina()
    {
        long user = await PlayerAsync();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        server.Execute($"INSERT OR REPLACE INTO player_stamina(user_no,stamina,last_gain) VALUES({user},97,{now + 657})");
        JsonObject reply = await server.CallGatewayAsync(60, new { }, user);
        Assert.Equal(97, reply["user_info"]!["user_stamina"]!.GetValue<long>());
        Assert.InRange(reply["user_info"]!["user_remind_time_stamina"]!.GetValue<long>(), 1, 300);
        long persisted = server.Scalar($"SELECT last_gain FROM player_stamina WHERE user_no={user}");
        Assert.InRange(persisted, now, now + 5);
    }

    [Theory]
    [InlineData(10, 101)]
    [InlineData(100, 101)]
    [InlineData(101, 101)]
    [InlineData(145, 145)]
    public async Task Level_up_refills_to_new_capacity_without_removing_overflow(long stamina, long expected)
    {
        long user = await PlayerAsync();
        long cost = server.Scalar("SELECT exp_to_next FROM master_level WHERE level=1");
        server.Execute($"INSERT OR REPLACE INTO player_stamina(user_no,stamina,last_gain) VALUES({user},{stamina},strftime('%s','now'))");
        using IServiceScope scope = server.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<DataSession>();
        session.BeginTransaction();
        var profiles = scope.ServiceProvider.GetRequiredService<PlayerProfileService>();
        profiles.AwardExperience(user, cost);
        Assert.Equal(2, profiles.GetAccount(user).Level);
        Assert.Equal(101, profiles.GetStaminaLimits(user).Max);
        Assert.Equal(expected, profiles.GetStamina(user).Stamina);
        session.Commit();
        Assert.Equal(expected, server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}"));
        JsonObject login = await server.CallGatewayAsync(60, new { }, user);
        Assert.Equal(101, login["user_info"]!["user_max_stamina"]!.GetValue<long>());
        Assert.Equal(expected, login["user_info"]!["user_stamina"]!.GetValue<long>());
    }

    [Fact]
    public async Task Multiple_levels_refill_to_new_capacity_including_hero_point_upgrades()
    {
        long user = await PlayerAsync();
        Assert.Equal(0, (await server.CallGatewayAsync(901, new { }, user))["errno"]!.GetValue<long>());
        long exp = server.Scalar("SELECT SUM(exp_to_next) FROM master_level WHERE level IN (1,2,3)");
        server.Execute($"INSERT OR REPLACE INTO player_stamina(user_no,stamina,last_gain) VALUES({user},12,strftime('%s','now'))");
        using IServiceScope scope = server.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<DataSession>();
        session.BeginTransaction();
        var profiles = scope.ServiceProvider.GetRequiredService<PlayerProfileService>();
        profiles.AwardExperience(user, exp);
        Assert.Equal(4, profiles.GetAccount(user).Level);
        Assert.Equal(105, profiles.GetStaminaLimits(user).Max);
        Assert.Equal(105, profiles.GetStamina(user).Stamina);
        session.Commit();
        JsonObject login = await server.CallGatewayAsync(60, new { }, user);
        Assert.Equal(103, login["user_info"]!["user_max_stamina"]!.GetValue<long>());
        Assert.Equal(1, login["user_info"]!["user_max_stamina_buff"]!.GetValue<long>());
    }

    [Fact]
    public async Task Existing_level_is_reflected_in_capacity_without_refilling_on_login()
    {
        long user = await PlayerAsync();
        server.Execute($"UPDATE gameuser SET level=18 WHERE user_no={user}");
        server.Execute($"INSERT OR REPLACE INTO player_stamina(user_no,stamina,last_gain) VALUES({user},50,strftime('%s','now'))");
        JsonObject login = await server.CallGatewayAsync(60, new { }, user);
        Assert.Equal(117, login["user_info"]!["user_max_stamina"]!.GetValue<long>());
        Assert.Equal(50, login["user_info"]!["user_stamina"]!.GetValue<long>());
    }

    [Fact]
    public async Task Experience_without_level_up_does_not_refill_stamina()
    {
        long user = await PlayerAsync();
        server.Execute($"INSERT OR REPLACE INTO player_stamina(user_no,stamina,last_gain) VALUES({user},12,strftime('%s','now'))");
        using IServiceScope scope = server.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<DataSession>();
        session.BeginTransaction();
        var profiles = scope.ServiceProvider.GetRequiredService<PlayerProfileService>();
        profiles.AwardExperience(user, 1);
        Assert.Equal(12, profiles.GetStamina(user).Stamina);
        session.Commit();
    }

    [Fact]
    public async Task Multiple_clear_applies_level_up_refill_after_reserving_all_stamina()
    {
        long user = await PlayerAsync();
        long exp = server.Scalar("SELECT exp_to_next FROM master_level WHERE level=1");
        server.Execute($"UPDATE gameuser SET exp={exp - 1} WHERE user_no={user}");
        server.Execute($"INSERT OR REPLACE INTO player_stamina(user_no,stamina,last_gain) VALUES({user},50,strftime('%s','now'))");
        Assert.Equal(0, (await server.CallGatewayAsync(71, new { stage_id = 3000001, stage_mode = 1, clear_count = 6 }, user))["errno"]!.GetValue<long>());
        Assert.True(server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}") < 50);
        JsonObject result = await server.CallGatewayAsync(72, new { stage_no = 3000001, stage_result = 1 }, user);
        long level = result["user_info"]!["user_level"]!.GetValue<long>();
        Assert.True(level >= 2);
        Assert.Equal(100 + level - 1, result["user_info"]!["user_max_stamina"]!.GetValue<long>());
        Assert.Equal(100 + level - 1, result["user_info"]!["user_stamina"]!.GetValue<long>());
    }

    [Fact]
    public async Task Stage_clear_returns_refilled_stamina_in_result_and_preserves_it_on_login()
    {
        long user = await PlayerAsync();
        long exp = server.Scalar("SELECT exp_to_next FROM master_level WHERE level=1");
        server.Execute($"UPDATE gameuser SET exp={exp - 1} WHERE user_no={user}");
        server.Execute($"INSERT OR REPLACE INTO player_stamina(user_no,stamina,last_gain) VALUES({user},50,strftime('%s','now'))");
        Assert.Equal(0, (await server.CallGatewayAsync(71, new { stage_id = 3000001, stage_mode = 1 }, user))["errno"]!.GetValue<long>());
        JsonObject result = await server.CallGatewayAsync(72, new { stage_no = 3000001, stage_result = 1 }, user);
        Assert.Equal(2, result["user_info"]!["user_level"]!.GetValue<long>());
        Assert.Equal(101, result["user_info"]!["user_stamina"]!.GetValue<long>());
        Assert.Equal(101, result["user_info"]!["user_max_stamina"]!.GetValue<long>());
        Assert.Equal(101, (await server.CallGatewayAsync(60, new { }, user))["user_info"]!["user_stamina"]!.GetValue<long>());
    }
}
