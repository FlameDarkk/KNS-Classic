using System.Text.Json.Nodes;
using KnS.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

/// <summary>Protocol 311 (Book of Secrets) and native +50% EXP boost across dungeons, stories, and ally missions.</summary>
[Collection(SharedServer.Name)]
public sealed class BookOfSecretsTests(ServerFixture server)
{
    private static readonly long[] NoPickups = [];
    private static readonly long[] MissionIds = [6210006L];
    private static readonly long[] CompletedStories = [7000001, 7000002, 7000003];

    private async Task<long> PlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        return user;
    }

    private long TotalHeroExp(long user) => server.Scalar(
        $"SELECT u.exp + COALESCE((SELECT SUM(l.exp_to_next) FROM master_level l WHERE l.level < u.level), 0)"
        + $" FROM gameuser u WHERE u.user_no={user}");

    private long TotalFellowExp(long user, long fellowNo) => server.Scalar(
        $"SELECT f.exp + COALESCE((SELECT SUM(l.exp_to_next) FROM master_fellow_level l WHERE l.grade=m.grade AND l.level<f.level), 0)"
        + $" FROM fellow f JOIN master_fellow m ON m.fellow_id=f.fellow_id WHERE f.user_no={user} AND f.rowid_alias={fellowNo - KnS.Data.WireIdentifiers.FellowNumberOffset}");

    private long ItemCount(long itemNo) =>
        server.Scalar($"SELECT COUNT(*) FROM item WHERE rowid_alias={itemNo - KnS.Data.WireIdentifiers.ItemNumberOffset}");

    private long GrantItem(long user, long itemId)
    {
        using IServiceScope scope = server.CreateScope();
        long number = scope.ServiceProvider.GetRequiredService<ItemRepository>().Add(user, itemId, claimed: true, new ItemStats(0, 0, 0));
        scope.ServiceProvider.GetRequiredService<KnS.Data.DataSession>().Commit();
        return number;
    }

    [Fact]
    public async Task Buying_and_using_books_consumes_item_sets_buff_blocks_overlap_and_allows_renewal_after_expiry()
    {
        long user = await PlayerAsync("book-lifecycle");
        server.Execute($"UPDATE gameuser SET cash=200 WHERE user_no={user}");

        // Buy 1h (1600011) and 3h (1600012) Books of Secrets via 904.
        long product1h = server.Scalar("SELECT product_id FROM master_friendship_shop WHERE reward_item_id=1600011 LIMIT 1");
        long product3h = server.Scalar("SELECT product_id FROM master_friendship_shop WHERE reward_item_id=1600012 LIMIT 1");
        JsonObject buy1 = await server.CallGatewayAsync(904, new { product_id = product1h }, user);
        JsonObject buy2 = await server.CallGatewayAsync(904, new { product_id = product3h }, user);
        Assert.Equal(0, buy1["errno"]!.GetValue<long>());
        Assert.Equal(0, buy2["errno"]!.GetValue<long>());
        long book1No = buy1["item_info"]!["item_no"]!.GetValue<long>();
        long book2No = buy2["item_info"]!["item_no"]!.GetValue<long>();

        // Locked book is refused and stays in inventory.
        Assert.Equal(0, (await server.CallGatewayAsync(84, new { item_no = book1No, item_lock = 1 }, user))["errno"]!.GetValue<long>());
        Assert.NotEqual(0, (await server.CallGatewayAsync(311, new { use_item_no = book1No }, user))["errno"]!.GetValue<long>());
        Assert.Equal(1, ItemCount(book1No));
        Assert.Equal(0, (await server.CallGatewayAsync(84, new { item_no = book1No, item_lock = 0 }, user))["errno"]!.GetValue<long>());

        // Non-book material (EXP potion 1600001) is refused.
        long potionNo = GrantItem(user, 1600001);
        Assert.NotEqual(0, (await server.CallGatewayAsync(311, new { use_item_no = potionNo }, user))["errno"]!.GetValue<long>());

        // Use 1h book -> consumes item, sets buff_type=2 to now + 3600, returned in 311 and 60.
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        JsonObject used = await server.CallGatewayAsync(311, new { use_item_no = book1No }, user);
        Assert.Equal(0, used["errno"]!.GetValue<long>());
        Assert.Equal(book1No, used["use_item_no"]!.GetValue<long>());
        JsonNode buff = Assert.Single(used["buff_etc_info"]!.AsArray(), b => b!["type"]!.GetValue<long>() == 2)!;
        Assert.InRange(buff["end_date_timestamp"]!.GetValue<long>(), before + 3595, before + 3610);
        Assert.Equal(0, ItemCount(book1No));

        JsonObject loaded = await server.CallGatewayAsync(60, new { }, user);
        JsonNode loadedBuff = Assert.Single(loaded["buff_etc_info"]!.AsArray(), b => b!["type"]!.GetValue<long>() == 2)!;
        Assert.Equal(buff["end_date_timestamp"]!.GetValue<long>(), loadedBuff["end_date_timestamp"]!.GetValue<long>());

        // Using a second book while active is refused and preserves the second book.
        Assert.NotEqual(0, (await server.CallGatewayAsync(311, new { use_item_no = book2No }, user))["errno"]!.GetValue<long>());
        Assert.Equal(1, ItemCount(book2No));

        // Once expired, a new book (3h) can be activated and updates end_time.
        server.Execute($"UPDATE user_buff SET end_time={before - 10} WHERE user_no={user} AND buff_type=2");
        JsonObject renewed = await server.CallGatewayAsync(311, new { use_item_no = book2No }, user);
        Assert.Equal(0, renewed["errno"]!.GetValue<long>());
        Assert.Equal(book2No, renewed["use_item_no"]!.GetValue<long>());
        JsonNode renewedBuff = Assert.Single(renewed["buff_etc_info"]!.AsArray(), b => b!["type"]!.GetValue<long>() == 2)!;
        Assert.InRange(renewedBuff["end_date_timestamp"]!.GetValue<long>(), before + 10795, before + 10815);
        Assert.Equal(0, ItemCount(book2No));
    }

    [Fact]
    public async Task Active_book_boosts_dungeon_story_and_mission_exp_by_50_percent_but_not_exp_potions()
    {
        long user = await PlayerAsync("book-exp-sources");
        long fellowNo;
        using (IServiceScope scope = server.CreateScope())
        {
            var fellows = scope.ServiceProvider.GetRequiredService<KnS.Game.Fellows.FellowService>();
            var slots = scope.ServiceProvider.GetRequiredService<FellowRepository>();
            fellowNo = fellows.Grant(user, 100001);
            slots.SetSlot(user, 3, 0, fellowNo);
            scope.ServiceProvider.GetRequiredService<KnS.Data.DataSession>().Commit();
        }

        // 1. Baseline dungeon clear (stage 3000001) without boost.
        long baseStageExp = server.Scalar("SELECT c87 FROM master_stage WHERE stage_id=3000001");
        Assert.True(baseStageExp > 0);
        long heroBefore = TotalHeroExp(user);
        long allyBefore = TotalFellowExp(user, fellowNo);
        server.Execute($"INSERT OR REPLACE INTO stage_start(user_no,stage_id) VALUES({user},3000001)");
        Assert.Equal(0, (await server.CallGatewayAsync(72, new { stage_no = 3000001, stage_mode = 1, stage_result = 1, reward_gold = 0, reward_items = NoPickups, event_items = NoPickups }, user))["errno"]!.GetValue<long>());
        Assert.Equal(baseStageExp, TotalHeroExp(user) - heroBefore);
        Assert.Equal(baseStageExp, TotalFellowExp(user, fellowNo) - allyBefore);

        // Activate 1h Book of Secrets via 311.
        long bookNo = GrantItem(user, 1600011);
        Assert.Equal(0, (await server.CallGatewayAsync(311, new { use_item_no = bookNo }, user))["errno"]!.GetValue<long>());

        // 2. Boosted dungeon clear: hero AND dungeon ally get base + (base / 2).
        long boostedStageExp = baseStageExp + (baseStageExp / 2);
        heroBefore = TotalHeroExp(user);
        allyBefore = TotalFellowExp(user, fellowNo);
        server.Execute($"INSERT OR REPLACE INTO stage_start(user_no,stage_id) VALUES({user},3000001)");
        Assert.Equal(0, (await server.CallGatewayAsync(72, new { stage_no = 3000001, stage_mode = 1, stage_result = 1, reward_gold = 0, reward_items = NoPickups, event_items = NoPickups }, user))["errno"]!.GetValue<long>());
        Assert.Equal(boostedStageExp, TotalHeroExp(user) - heroBefore);
        Assert.Equal(boostedStageExp, TotalFellowExp(user, fellowNo) - allyBefore);

        // 3. Boosted Story completion (7000004 after clearing 7000001-7000003 + 3000031): hero gets base + (base / 2).
        foreach (long done in CompletedStories)
        {
            server.Execute($"INSERT INTO story_completion(user_no, story_id) VALUES({user}, {done})");
        }
        server.Execute($"INSERT OR REPLACE INTO story_progress(user_no, story_id, quest_count) VALUES({user}, 7000004, 1)");
        long baseStoryExp = server.Scalar("SELECT experience FROM master_story_quest WHERE story_id=7000004");
        Assert.True(baseStoryExp > 0);
        heroBefore = TotalHeroExp(user);
        JsonObject storyReply = await server.CallGatewayAsync(152, new { story_id = 7000004 }, user);
        Assert.Equal(0, storyReply["errno"]!.GetValue<long>());
        Assert.Equal(baseStoryExp + (baseStoryExp / 2), TotalHeroExp(user) - heroBefore);

        // 4. Boosted Ally Mission claim (6210006 has native EXP = 89 -> 89 + 44 = 133).
        long missionFellowNo;
        using (IServiceScope scope = server.CreateScope())
        {
            missionFellowNo = scope.ServiceProvider.GetRequiredService<KnS.Game.Fellows.FellowService>().Grant(user, 100001);
            scope.ServiceProvider.GetRequiredService<KnS.Data.DataSession>().Commit();
        }
        server.Execute($"DELETE FROM fellow_mission WHERE user_no={user}");
        server.Execute($"INSERT INTO fellow_mission(user_no,mission_id,status,fellows,soldiers,end_time,success_rate,rewards) VALUES({user},6210006,1,'{missionFellowNo}','',1,100,'1900008:10')");
        JsonObject missionReply = await server.CallGatewayAsync(325, new { mission_id_list = MissionIds, use_cash = 0 }, user);
        Assert.Single(missionReply["success_fellow_mission_infos"]!.AsArray());
        Assert.Equal(89 + (89 / 2), TotalFellowExp(user, missionFellowNo));

        // 5. EXP potion (Protocol 310, 1600001 = 400 EXP) is NOT boosted while Book of Secrets is active.
        long potionFellowNo;
        using (IServiceScope scope = server.CreateScope())
        {
            potionFellowNo = scope.ServiceProvider.GetRequiredService<KnS.Game.Fellows.FellowService>().Grant(user, 100472);
            scope.ServiceProvider.GetRequiredService<KnS.Data.DataSession>().Commit();
        }
        long potionNo = GrantItem(user, 1600001);
        JsonObject levelUp = await server.CallGatewayAsync(310, new { fellow_no = potionFellowNo, use_item_no_list = new[] { potionNo } }, user);
        Assert.Equal(0, levelUp["errno"]!.GetValue<long>());
        Assert.Equal(400, TotalFellowExp(user, potionFellowNo));

        // 6. Once expired, dungeon clear returns to unboosted baseStageExp.
        server.Execute($"UPDATE user_buff SET end_time=1 WHERE user_no={user} AND buff_type=2");
        heroBefore = TotalHeroExp(user);
        server.Execute($"INSERT OR REPLACE INTO stage_start(user_no,stage_id) VALUES({user},3000001)");
        Assert.Equal(0, (await server.CallGatewayAsync(72, new { stage_no = 3000001, stage_mode = 1, stage_result = 1, reward_gold = 0, reward_items = NoPickups, event_items = NoPickups }, user))["errno"]!.GetValue<long>());
        Assert.Equal(baseStageExp, TotalHeroExp(user) - heroBefore);
    }
}
