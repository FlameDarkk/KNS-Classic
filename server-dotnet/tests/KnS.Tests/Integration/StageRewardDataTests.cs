namespace KnS.Tests.Integration;

/// <summary>
/// master_stage_reward comes from dt_stage_pkg_140 (tools/dump/dump_drops.py). Every slot must name a real
/// equipment, book/material, etc item or ally, with a 0-100 chance, or StageRewardService.RollStage would hand out
/// ids the client cannot show (table 6 stages 3303083 / 3303091 are stored misaligned; stage 3200110 lists 301091).
/// </summary>
[Collection(SharedServer.Name)]
public class StageRewardDataTests(ServerFixture server)
{
    [Fact]
    public void Every_reward_item_is_a_known_item_or_ally()
    {
        Assert.True(server.Scalar("SELECT COUNT(*) FROM master_stage_reward") > 0);
        Assert.Equal(0, server.Scalar(
            "SELECT COUNT(*) FROM master_stage_reward r WHERE " +
            "NOT EXISTS (SELECT 1 FROM master_item i WHERE i.item_id = r.item_id) AND " +
            "NOT EXISTS (SELECT 1 FROM master_material m WHERE m.item_id = r.item_id) AND " +
            "NOT EXISTS (SELECT 1 FROM master_etc_item e WHERE e.item_id = r.item_id) AND " +
            "NOT EXISTS (SELECT 1 FROM master_fellow f WHERE f.fellow_id = r.item_id)"));
    }

    [Fact]
    public void Every_reward_chance_is_a_percentage()
    {
        Assert.Equal(0, server.Scalar("SELECT COUNT(*) FROM master_stage_reward WHERE chance < 0 OR chance > 100"));
    }
}
