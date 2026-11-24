using System.Text.Json.Nodes;
using KnS.Data.Repositories;
using KnS.Game.Models;
using KnS.Game.Stages;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

/// <summary>
/// The clear screen's reward row: item_infos are the chests (equipment, materials, Gold Boxes). Counted event items such as the
/// World Tree Branch are drawn beside the row with the offered count (DrawResult@0xe8334), so they are credited in full and never chests.
/// </summary>
[Collection(SharedServer.Name)]
public class StageChestTests(ServerFixture server)
{
    private static readonly long[] TwoCountedPickups = [0, 0];

    [Fact]
    public void Branch_pickups_open_no_chests()
    {
        var equipment = new ItemInfo { ItemNumber = 2043, ItemId = 900091 };
        var branches = new EventOfferRecord { ItemNumber = 1000007, ItemId = 1901007, Count = 3 };
        IReadOnlyList<object> chests = StageRewardService.OrderChests(
            [JsonValue.Create(0), JsonValue.Create(2043), JsonValue.Create(0), JsonValue.Create(0)], [equipment], [branches]);
        Assert.Single(chests);
        Assert.Same(equipment, chests[0]);
    }

    [Fact]
    public void Gold_boxes_stay_chests_in_report_order()
    {
        var equipment = new ItemInfo { ItemNumber = 2043, ItemId = 900091 };
        var box = new EventOfferRecord { ItemNumber = 1000009, ItemId = 1900015, Count = 250 };
        var branches = new EventOfferRecord { ItemNumber = 1000007, ItemId = 1901007, Count = 2 };
        IReadOnlyList<object> chests = StageRewardService.OrderChests(
            [JsonValue.Create(1000009), JsonValue.Create(0), JsonValue.Create(2043)], [equipment], [box, branches]);
        Assert.Equal(2, chests.Count);
        Assert.Equal(new CountedItemInfo(1000009, 1900015, 250), chests[0]);
        Assert.Same(equipment, chests[1]);
    }

    [Fact]
    public void Result_row_never_exceeds_the_four_chests_the_client_can_tap()
    {
        ItemInfo[] items = Enumerable.Range(0, 6).Select(i => new ItemInfo { ItemNumber = 2040 + i, ItemId = 900091 }).ToArray();
        JsonNode?[] reported = items.Select(item => (JsonNode?)JsonValue.Create(item.ItemNumber)).ToArray();
        Assert.Equal(StageRewardService.MaxResultChests, StageRewardService.OrderChests(reported, items, []).Count);
    }

    [Fact]
    public void Dropped_allies_take_chest_slots_of_the_four()
    {
        ItemInfo[] items = Enumerable.Range(0, 4).Select(i => new ItemInfo { ItemNumber = 2040 + i, ItemId = 900091 }).ToArray();
        JsonNode?[] reported = items.Select(item => (JsonNode?)JsonValue.Create(item.ItemNumber)).ToArray();
        Assert.Equal(3, StageRewardService.OrderChests(reported, items, [], droppedFellows: 1).Count);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(4, 0)]
    [InlineData(3, 1)]
    public async Task Boss_branches_pay_the_whole_offer_after_partial_or_no_pickup_and_do_not_pay_twice(long count, int picked)
    {
        (long user, long offer, long before) = await StartWithBranchOfferAsync($"stage-boss-branches-{count}-{picked}", count);
        var report = new
        {
            stage_no = Stage, stage_mode = 1, stage_result = 1, reward_gold = 0,
            reward_items = Array.Empty<long>(), event_items = Enumerable.Repeat(offer, picked).ToArray(),
        };
        JsonObject result = await server.CallGatewayAsync(72, report, user);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Equal(before + count, Branches(user));
        Assert.Contains(result["update_item_etc_info"]!.AsArray(),
            entry => entry!["item_id"]!.GetValue<long>() == 1901007 && entry["count"]!.GetValue<long>() == before + count);
        JsonNode branch = Assert.Single(result["drop_event_item_list"]!.AsArray())!;
        Assert.Equal(1901007, branch["item_id"]!.GetValue<long>());
        Assert.Equal(count, branch["count"]!.GetValue<long>());
        await server.CallGatewayAsync(72, report, user);
        Assert.Equal(before + count, Branches(user));
    }

    [Fact]
    public async Task Clear_grants_unpicked_equipment_even_beyond_the_visible_chest_limit()
    {
        (long user, _, _) = await StartWithBranchOfferAsync("stage-unpicked-equipment", 1);
        server.Execute($"DELETE FROM stage_event_offer WHERE user_no={user}");
        for (int i = 0; i < 6; i++)
            server.Execute($"INSERT INTO item(user_no,item_id,claimed) VALUES({user},900091,0)");
        long before = server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user} AND claimed=1");
        JsonObject result = await server.CallGatewayAsync(72, new
        {
            stage_no = Stage, stage_mode = 1, stage_result = 1, reward_gold = 0,
            reward_items = Array.Empty<long>(), event_items = Array.Empty<long>(),
        }, user);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Equal(4, result["item_infos"]!.AsArray().Count);
        Assert.Equal(before + 6, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user} AND claimed=1"));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM item WHERE user_no={user} AND claimed=0"));
    }

    [Fact]
    public async Task Clear_credits_every_offered_branch_without_a_chest()
    {
        (long user, long offer, long before) = await StartWithBranchOfferAsync("stage-chest-offered", 4);

        // Patched client reports one offer entry per picked-up branch in event_items and no 0s in reward_items.
        JsonObject result = await server.CallGatewayAsync(72, new
        {
            stage_no = Stage, stage_mode = 1, stage_result = 1, reward_gold = 0,
            reward_items = Array.Empty<long>(), event_items = new[] { offer, offer },
        }, user);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Empty(result["item_infos"]!.AsArray());
        Assert.Equal(before + 4, Branches(user));
    }

    [Fact]
    public async Task Clear_credits_picked_up_zeros_when_unpatched_client_reports_one_event_item()
    {
        (long user, long offer, long before) = await StartWithBranchOfferAsync("stage-chest-unpatched", 4);

        JsonObject result = await server.CallGatewayAsync(72, new
        {
            stage_no = Stage, stage_mode = 1, stage_result = 1, reward_gold = 0,
            reward_items = TwoCountedPickups, event_items = new[] { offer },
        }, user);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Empty(result["item_infos"]!.AsArray());
        Assert.Equal(before + 4, Branches(user));
    }

    [Fact]
    public async Task Clear_credits_branches_left_on_the_ground()
    {
        (long user, _, long before) = await StartWithBranchOfferAsync("stage-chest-none", 4);

        JsonObject result = await server.CallGatewayAsync(72, new
        {
            stage_no = Stage, stage_mode = 1, stage_result = 1, reward_gold = 0,
            reward_items = Array.Empty<long>(), event_items = Array.Empty<long>(),
        }, user);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Empty(result["item_infos"]!.AsArray());
        Assert.Equal(before + 4, Branches(user));
    }

    [Fact]
    public async Task Ally_chest_is_granted_even_when_not_picked_up()
    {
        (long user, _, _) = await StartWithBranchOfferAsync("stage-chest-ally", 1);
        server.Execute($"DELETE FROM stage_event_offer WHERE user_no={user}");
        server.Execute($"INSERT INTO stage_event_offer(user_no,item_id,count) VALUES({user},100417,1)");
        long allyOffer = server.Scalar($"SELECT MAX(rowid_alias) FROM stage_event_offer WHERE user_no={user}") + 1_000_000;

        JsonObject skipped = await server.CallGatewayAsync(72, new
        {
            stage_no = Stage, stage_mode = 1, stage_result = 1, reward_gold = 0,
            reward_items = Array.Empty<long>(), event_items = Array.Empty<long>(),
        }, user);
        Assert.Equal(0, skipped["errno"]!.GetValue<long>());
        Assert.Single(skipped["drop_fellow_infos"]!.AsArray());
        Assert.Empty(skipped["item_infos"]!.AsArray());

        await StartWithBranchOfferAsync("stage-chest-ally", 1);
        server.Execute($"DELETE FROM stage_event_offer WHERE user_no={user}");
        server.Execute($"INSERT INTO stage_event_offer(user_no,item_id,count) VALUES({user},100417,1)");
        long pickedOffer = server.Scalar($"SELECT MAX(rowid_alias) FROM stage_event_offer WHERE user_no={user}") + 1_000_000;

        JsonObject picked = await server.CallGatewayAsync(72, new
        {
            stage_no = Stage, stage_mode = 1, stage_result = 1, reward_gold = 0,
            reward_items = new[] { pickedOffer }, event_items = Array.Empty<long>(),
        }, user);
        Assert.Equal(0, picked["errno"]!.GetValue<long>());
        Assert.Single(picked["drop_fellow_infos"]!.AsArray());
        Assert.Empty(picked["item_infos"]!.AsArray());
    }

    [Fact]
    public async Task Kind_missing_from_event_items_is_still_credited_when_carriers_were_picked_up()
    {
        (long user, _, long before) = await StartWithBranchOfferAsync("stage-chest-unreported", 2);
        JsonObject result = await server.CallGatewayAsync(72, new
        {
            stage_no = Stage, stage_mode = 1, stage_result = 1, reward_gold = 0,
            reward_items = TwoCountedPickups, event_items = Array.Empty<long>(),
        }, user);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Empty(result["item_infos"]!.AsArray());
        Assert.Equal(before + 2, Branches(user));
    }

    [Fact]
    public async Task Failed_run_credits_no_branches()
    {
        (long user, long offer, long before) = await StartWithBranchOfferAsync("stage-chest-failed", 3);
        JsonObject result = await server.CallGatewayAsync(72, new
        {
            stage_no = Stage, stage_mode = 1, stage_result = 0, reward_gold = 0,
            reward_items = TwoCountedPickups, event_items = new[] { offer },
        }, user);
        Assert.Equal(0, result["errno"]!.GetValue<long>());
        Assert.Equal(before, Branches(user));
    }

    [Fact]
    public async Task Frozen_chapel_native_roll_counts_can_drop_four_chests_and_accumulate_repeated_slots()
    {
        using var scope = server.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataRepository>();
        StageDropRollCounts counts = master.GetStageDropRollCounts(3250310);
        Assert.Equal(new StageDropRollCounts(1, 3, 1, 1), counts);
        StageGoldSettings? gold = master.GetStageGoldSettings(3250310);
        Assert.NotNull(gold);
        Assert.Equal(100, gold.GoldBoxMin);
        Assert.Equal(900, gold.GoldBoxMax);

        long user = (await server.CallGatewayAsync(51, new { hub_did = "stage-chest-frozen-chapel" }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        // Sequence (only calls with exclusiveMax > 1 consume): Group1 rolls=3 (2 -> 1+2=3),
        // Group1 hits: 0 (slot0 110001), 90 (slot3 100181), 0 (slot0 110001 again);
        // Group2 hit: 0 (slot10 Gold Box 1900015); Gold Box amount offset 58 -> 158 G.
        var rng = new SequenceRandom([2, 0, 90, 0, 0, 58]);
        StageRewardService rewards = Microsoft.Extensions.DependencyInjection.ActivatorUtilities
            .CreateInstance<StageRewardService>(scope.ServiceProvider, rng);
        DropItemList offers = rewards.OfferDrops(user, 3250310, 12);
        Assert.Equal(4, offers.Chests.Count);
        Assert.Equal(3, offers.Chests.Count(d => d.ItemId <= 199999));
        Assert.Equal(1, offers.Chests.Count(d => d.ItemId == 1900015));
        PendingDrops pending = rewards.GetPendingDrops(user);
        EventOfferRecord goldBox = Assert.Single(pending.CountedItems, o => o.ItemId == 1900015);
        Assert.Equal(158, goldBox.Count);
    }

    private sealed class SequenceRandom(long[] values) : KnS.Game.Randomness.IRandomSource
    {
        private int _index;
        public long NextBelow(long exclusiveMax)
        {
            if (exclusiveMax <= 1) return 0;
            long v = _index < values.Length ? values[_index++] : 0;
            return Math.Clamp(v, 0, exclusiveMax - 1);
        }
    }

    private const long Stage = 3001004; // Exploding Mountain

    private async Task<(long User, long Offer, long Before)> StartWithBranchOfferAsync(string device, long count)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        JsonObject start = await server.CallGatewayAsync(71, new { friend_type = 0, stage_id = Stage, stage_mode = 1, friend_user_no = 0 }, user);
        Assert.Equal(0, start["errno"]!.GetValue<long>());
        server.Execute($"DELETE FROM stage_event_offer WHERE user_no={user}");
        server.Execute($"DELETE FROM stage_roll WHERE user_no={user}");
        server.Execute($"DELETE FROM item WHERE user_no={user} AND claimed=0");
        server.Execute($"INSERT INTO stage_event_offer(user_no,item_id,count) VALUES({user},1901007,{count})");
        long offer = server.Scalar($"SELECT MAX(rowid_alias) FROM stage_event_offer WHERE user_no={user}") + 1_000_000;
        return (user, offer, Branches(user));
    }

    private long Branches(long user) =>
        server.Scalar($"SELECT COALESCE(SUM(count),0) FROM user_goods WHERE user_no={user} AND item_id=1901007");
}
