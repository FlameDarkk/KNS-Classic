using KnS.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnS.Tests.Integration;

public class MasterSeedUpgradeTests
{
    [Fact]
    public void Existing_database_imports_missing_event_rows_without_resetting_players_or_master_values()
    {
        string path = Path.Combine(Path.GetTempPath(), "kns-seed-upgrade-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var options = new DatabaseOptions { SqlitePath = path };
        try
        {
            using (DataSession old = new GameDatabase(options, NullLogger<GameDatabase>.Instance).OpenSession())
            {
                old.Execute("DELETE FROM master_stage WHERE stage_id>=3200000 AND stage_id<3300000");
                old.Execute("DELETE FROM master_stage_monster WHERE stage_id>=3200000 AND stage_id<3300000");
                old.Execute("UPDATE master_stage SET c86=77 WHERE stage_id=3000001");
                old.Execute("UPDATE master_stage_attainment SET normal_reward_count=99 WHERE area_id=3000000");
                old.Execute("UPDATE master_item SET sell_price=999 WHERE item_id=200001");
                old.Execute("INSERT INTO gameuser(user_no,hub_did,nickname,gold,cash) VALUES(999999,'upgrade-sentinel','Saved player',1266,9067)");
            }
            for (int restart = 0; restart < 2; restart++)
            {
                using DataSession current = new GameDatabase(options, NullLogger<GameDatabase>.Instance).OpenSession();
                Assert.Equal(134, current.QueryFirstOrDefault<long>("SELECT COUNT(*) FROM master_stage WHERE stage_id>=3200000 AND stage_id<3300000"));
                Assert.Equal(134, current.QueryFirstOrDefault<long>("SELECT COUNT(*) FROM master_stage_monster WHERE stage_id>=3200000 AND stage_id<3300000"));
                Assert.Equal(77, current.QueryFirstOrDefault<long>("SELECT c86 FROM master_stage WHERE stage_id=3000001"));
                // Seeded native values (ReplaceStoredRows) reach existing databases: area 3000000 pays 3 Gems and Long Sword sells for 100 Gold.
                Assert.Equal(3, current.QueryFirstOrDefault<long>("SELECT normal_reward_count FROM master_stage_attainment WHERE area_id=3000000"));
                Assert.Equal(100, current.QueryFirstOrDefault<long>("SELECT sell_price FROM master_item WHERE item_id=200001"));
                Assert.Equal(1266, current.QueryFirstOrDefault<long>("SELECT gold FROM gameuser WHERE user_no=999999"));
                Assert.Equal(9067, current.QueryFirstOrDefault<long>("SELECT cash FROM gameuser WHERE user_no=999999"));
                Assert.Equal("Saved player", current.QueryFirstOrDefault<string>("SELECT nickname FROM gameuser WHERE user_no=999999"));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}

