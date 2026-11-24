using System.Text.Json.Nodes;
using KnS.Data.Repositories;
using KnS.Game.Randomness;
using KnS.Game.Models;
using KnS.Game.Stages;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

[Collection(SharedServer.Name)]
public class EventDungeonTests(ServerFixture server)
{
    [Theory]
    [InlineData(3200810)]
    [InlineData(3200820)]
    [InlineData(3200830)]
    [InlineData(3200840)]
    [InlineData(3200910)]
    [InlineData(3200920)]
    [InlineData(3200930)]
    [InlineData(3200940)]
    [InlineData(3201000)]
    [InlineData(3201010)]
    [InlineData(3200110)]
    public async Task Incomplete_original_entries_refuse_even_an_open_calendar_without_spending(long stage)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = $"missing-event-map-{stage}" }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        // Disposable database only. Simulate an old Open all calendar, including undated entries.
        server.Execute($"INSERT INTO master_event_schedule(stage_id,start_time,end_time,duration_seconds,period_seconds) VALUES({stage},0,2140000000,86400,86400)");
        try
        {
            long stamina = server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}");
            JsonObject result = await server.CallGatewayAsync(71, new { stage_id = stage, stage_mode = 11 }, user);
            Assert.NotEqual(0, result["errno"]!.GetValue<long>());
            Assert.Equal(stamina, server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}"));
            Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM stage_roll WHERE user_no={user}"));
            Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM event_stage_play WHERE user_no={user}"));
        }
        finally { server.Execute($"DELETE FROM master_event_schedule WHERE stage_id={stage}"); }
    }

    [Theory]
    [InlineData(3250300, 110001, 0)]
    [InlineData(3200280, 300241, 0)]
    [InlineData(3250200, 1900015, 160)]
    [InlineData(3250210, 1900015, 1000)]
    [InlineData(3250220, 1900015, 2500)]
    [InlineData(3250400, 1900015, 160)]
    [InlineData(3250410, 1900015, 1000)]
    [InlineData(3250420, 1900015, 2500)]
    public async Task Event_rewards_use_selected_difficulty_and_settle_once(long stage, long expectedReward, long expectedBoxGold)
    {
        using var scope = server.CreateScope();
        EventStageSchedule rule = scope.ServiceProvider.GetRequiredService<EventStageRepository>().Get(stage)!;
        long user = (await server.CallGatewayAsync(51, new { hub_did = $"event-reward-{stage}" }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        try
        {
            server.Execute($"UPDATE master_event_schedule SET start_time=0,end_time=2140000000,period_seconds=86400,duration_seconds=86400 WHERE stage_id={stage}");
            Assert.NotEqual(0, (await server.CallGatewayAsync(72, new { stage_no = stage, reward_gold = 500 }, user))["errno"]!.GetValue<long>());
            JsonObject start = await server.CallGatewayAsync(71, new { friend_type = 0, stage_id = stage, stage_mode = 11, friend_user_no = 0 }, user);
            Assert.Equal(0, start["errno"]!.GetValue<long>());
            StageRewardService rewards = ActivatorUtilities.CreateInstance<StageRewardService>(scope.ServiceProvider, new MinimumRandom());
            DropItemList offers = rewards.OfferDrops(user, stage, 11);
            rewards.OfferEventItems(user);
            PendingDrops pending = rewards.GetPendingDrops(user);
            Assert.Contains(pending.Rolled, row => row.ItemId == expectedReward);
            long gold = server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");
            object parameters = new
            {
                stage_no = stage + 1, stage_mode = 11, stage_result = 1, reward_gold = 123,
                reward_items = offers.Chests.Concat(offers.BossDrops).Select(row => row.ItemNumber).ToArray(),
                event_items = pending.CountedItems.Select(row => row.ItemNumber).ToArray(),
            };
            JsonObject result = await server.CallGatewayAsync(72, parameters, user);
            Assert.Equal(0, result["errno"]!.GetValue<long>());
            if (expectedReward == 110001)
                Assert.Contains(result["drop_fellow_infos"]!.AsArray(), row => row!["fellow_id"]!.GetValue<long>() == 110001);
            if (expectedReward == 300241)
                Assert.Contains(result["item_infos"]!.AsArray(), row => row!["item_id"]!.GetValue<long>() / 100000 == 3);
            long paidGold = server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");
            if (expectedBoxGold > 0)
            {
                var chests = result["item_infos"]!.AsArray();
                Assert.Equal(2, chests.Count);
                Assert.All(chests, chest =>
                {
                    Assert.Equal(expectedReward, chest!["item_id"]!.GetValue<long>());
                    Assert.Equal(expectedBoxGold, chest["value"]!.GetValue<long>());
                });
                Assert.Equal(gold + 123 + 2 * expectedBoxGold, paidGold);
            }
            else
            {
                Assert.True(paidGold >= gold + 123);
            }
            Assert.Equal(1, server.Scalar($"SELECT complete FROM event_stage_play WHERE user_no={user}"));
            Assert.NotEqual(0, (await server.CallGatewayAsync(72, parameters, user))["errno"]!.GetValue<long>());
            Assert.Equal(paidGold, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}"));
            Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM stage_roll WHERE user_no={user}"));
        }
        finally
        {
            server.Execute($"UPDATE master_event_schedule SET start_time={rule.StartTime},end_time={rule.EndTime},duration_seconds={rule.DurationSeconds},period_seconds={rule.PeriodSeconds} WHERE stage_id={stage}");
        }
    }

    [Theory]
    [InlineData(3250100, 11)]
    [InlineData(3250110, 12)]
    [InlineData(3250120, 13)]
    [InlineData(3250330, 14)]
    [InlineData(3200280, 15)]
    public async Task Captured_event_enter_mode_is_accepted_only_for_event_stages(long stage, long mode)
    {
        using var scope = server.CreateScope();
        EventStageSchedule rule = scope.ServiceProvider.GetRequiredService<EventStageRepository>().Get(stage)!;
        long user = (await server.CallGatewayAsync(51, new { hub_did = $"event-captured-enter-{stage}" }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        try
        {
            server.Execute($"UPDATE master_event_schedule SET start_time=0,end_time=2140000000,period_seconds=86400,duration_seconds=86400 WHERE stage_id={stage}");
            long stamina = server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}");
            JsonObject refused = await server.CallGatewayAsync(71,
                new { friend_type = 0, stage_id = 3000001, stage_mode = mode, friend_user_no = 0 }, user);
            Assert.NotEqual(0, refused["errno"]!.GetValue<long>());
            Assert.Equal(stamina, server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}"));
            JsonObject entered = await server.CallGatewayAsync(71,
                new { friend_type = 0, stage_id = stage, stage_mode = mode, friend_user_no = 0 }, user);
            Assert.Equal(0, entered["errno"]!.GetValue<long>());
            Assert.Equal(stage, entered["stage_info"]!["stage_no"]!.GetValue<long>());
            Assert.Equal(13, entered["stage_info"]![$"monster_info_{stage}"]!.AsArray().Count);
            Assert.Equal(13, entered["stage_info"]![$"monster_info_{stage + 1}"]!.AsArray().Count);
            Assert.Equal(stage, server.Scalar($"SELECT stage_id FROM event_stage_play WHERE user_no={user}"));
        }
        finally
        {
            server.Execute($"UPDATE master_event_schedule SET start_time={rule.StartTime},end_time={rule.EndTime},duration_seconds={rule.DurationSeconds},period_seconds={rule.PeriodSeconds} WHERE stage_id={stage}");
        }
    }

    [Fact]
    public void Captured_equipment_and_gold_box_pickups_both_appear_in_chest_order()
    {
        var equipment = new ItemInfo { ItemNumber = 2043, ItemId = 900091 };
        var goldBox = new EventOfferRecord { ItemNumber = 1000006, ItemId = 1900015, Count = 750 };
        IReadOnlyList<object> chests = StageRewardService.OrderChests(
            [JsonValue.Create(2043), JsonValue.Create(1000006)], [equipment], [goldBox]);
        Assert.Equal(2, chests.Count);
        Assert.Same(equipment, chests[0]);
        Assert.Equal(new CountedItemInfo(1000006, 1900015, 750), chests[1]);
    }

    private sealed class MinimumRandom : IRandomSource
    {
        public long NextBelow(long exclusiveMax)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMax);
            return 0;
        }
    }

    [Fact]
    public void Original_weekly_windows_use_Korean_midnight_and_exact_boundaries()
    {
        using var scope = server.CreateScope();
        EventStageSchedule rule = scope.ServiceProvider.GetRequiredService<EventStageRepository>().Get(3250000)!;
        Assert.Equal(21, server.Scalar("SELECT COUNT(*) FROM master_event_schedule"));
        Assert.Equal(134, server.Scalar("SELECT COUNT(*) FROM master_stage WHERE stage_id>=3200000 AND stage_id<3300000"));
        Assert.Equal(134, server.Scalar("SELECT COUNT(*) FROM master_stage_monster WHERE stage_id>=3200000 AND stage_id<3300000"));
        long tuesday = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.FromHours(9)).ToUnixTimeSeconds();
        Assert.False(rule.IsOpen(tuesday - 1));
        Assert.True(rule.IsOpen(tuesday));
        Assert.True(rule.IsOpen(tuesday + 86399));
        Assert.False(rule.IsOpen(tuesday + 86400));
        Assert.True(rule.IsOpen(tuesday + 604800));
        Assert.False(rule.IsOpen(rule.StartTime - 1));
        Assert.False(rule.IsOpen(rule.EndTime));
        Assert.True(rule.EndTime < int.MaxValue);
        Assert.Null(scope.ServiceProvider.GetRequiredService<EventStageRepository>().Get(3250001));
        EventStageSchedule pit = scope.ServiceProvider.GetRequiredService<EventStageRepository>().Get(3200280)!;
        EventStageSchedule nextPit = scope.ServiceProvider.GetRequiredService<EventStageRepository>().Get(3200380)!;
        Assert.True(pit.IsOpen(pit.StartTime));
        Assert.True(pit.IsOpen(pit.StartTime + 3599));
        Assert.False(pit.IsOpen(pit.StartTime + 3600));
        Assert.False(nextPit.IsOpen(pit.StartTime));
        Assert.True(nextPit.IsOpen(pit.StartTime + 8 * 3600));
        Assert.True(pit.IsOpen(pit.StartTime + 16 * 3600));
    }

    [Fact]
    public async Task Event_entry_spends_table_stamina_counts_daily_and_refusals_preserve_both()
    {
        using var scope = server.CreateScope();
        EventStageSchedule rule = scope.ServiceProvider.GetRequiredService<EventStageRepository>().Get(3250000)!;
        long user = (await server.CallGatewayAsync(51, new { hub_did = "event-entry" }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { user_no = user }, user);
        long other = (await server.CallGatewayAsync(51, new { hub_did = "event-entry-other" }))["user_no"]!.GetValue<long>();
        try
        {
            // Change only the disposable test master's window so gateway tests
            // remain deterministic on every weekday and every host clock.
            server.Execute("UPDATE master_event_schedule SET start_time=0,end_time=2140000000,"
                + "period_seconds=86400,duration_seconds=86400 WHERE stage_id=3250000");
            JsonObject first = await server.CallGatewayAsync(71, new { stage_id = 3250000, stage_mode = 11 }, user);
            Assert.Equal(0, first["errno"]!.GetValue<long>());
            Assert.Equal(28, first["user_info"]!["user_stamina"]!.GetValue<long>());
            Assert.Equal(5300004, first["daily_quest_info"]!["quest_id"]!.GetValue<long>());
            Assert.Equal(1, first["daily_quest_info"]!["quest_count"]!.GetValue<long>());
            Assert.NotEqual(10, first["stage_info"]!["monster_info_3250000"]![0]!["MaxHP"]!.GetValue<long>());
            Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM daily_quest_progress WHERE user_no={other} AND quest_id=5300004"));

            server.Execute("UPDATE master_event_schedule SET end_time=1 WHERE stage_id=3250000");
            JsonObject closed = await server.CallGatewayAsync(71, new { stage_id = 3250000, stage_mode = 11 }, user);
            Assert.NotEqual(0, closed["errno"]!.GetValue<long>());
            Assert.Equal(28, server.Scalar($"SELECT stamina FROM player_stamina WHERE user_no={user}"));
            Assert.Equal(1, server.Scalar($"SELECT quest_count FROM daily_quest_progress WHERE user_no={user} AND quest_id=5300004"));

            server.Execute("UPDATE master_event_schedule SET end_time=2140000000 WHERE stage_id=3250000");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Assert.Equal(0, (await server.CallGatewayAsync(71, new { stage_id = 3250000, stage_mode = 11 }, user))["errno"]!.GetValue<long>());
            }
            JsonObject claim = await server.CallGatewayAsync(143, new { quest_id = 5300004 }, user);
            Assert.Equal(0, claim["errno"]!.GetValue<long>());
            Assert.Equal(1, claim["daily_quest_info"]!["complete"]!.GetValue<long>());
            Assert.NotEqual(0, (await server.CallGatewayAsync(143, new { quest_id = 5300004 }, user))["errno"]!.GetValue<long>());

            server.Execute($"UPDATE player_stamina SET stamina=0,last_gain={DateTimeOffset.UtcNow.ToUnixTimeSeconds()} WHERE user_no={user}");
            Assert.NotEqual(0, (await server.CallGatewayAsync(71, new { stage_id = 3250000, stage_mode = 11 }, user))["errno"]!.GetValue<long>());
            Assert.Equal(3, server.Scalar($"SELECT quest_count FROM daily_quest_progress WHERE user_no={user} AND quest_id=5300004"));
            Assert.NotEqual(0, (await server.CallGatewayAsync(71, new { stage_id = 3250001 }, user))["errno"]!.GetValue<long>());
            Assert.NotEqual(0, (await server.CallGatewayAsync(71, new { stage_id = 3200800 }, user))["errno"]!.GetValue<long>());
        }
        finally
        {
            server.Execute($"UPDATE master_event_schedule SET start_time={rule.StartTime},end_time={rule.EndTime},"
                + $"duration_seconds={rule.DurationSeconds},period_seconds={rule.PeriodSeconds} WHERE stage_id=3250000");
        }
    }
}
