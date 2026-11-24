using System.Text.Json;
using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

/// <summary>
/// Opt-in drop-rate measurement: many instant clears (real 71 start + 72 clear) per stage on the disposable test
/// database, counting what each run actually credits. Runs only when KNS_DROP_SIM names an output JSON path;
/// KNS_DROP_SIM_RUNS sets the runs per stage (default 1000). Used to check the farming guide against the server.
/// </summary>
[Collection(SharedServer.Name)]
public class DropRateSimulation(ServerFixture server)
{
    private static readonly (long Stage, long Mode)[] Stages =
    [
        (3250200, 11), (3250210, 11), (3250220, 11), (3250400, 11), // Forest of Hidden Treasures (Fri / Sun)
        (3250020, 11), (3250120, 11), (3250330, 11), (3200140, 11), (3200200, 11), // Tower, Furnace, Chapel, Bastille, Eerie Cavern
        (3004001, 1), (3004006, 1), (3003001, 1), (3003083, 1), (3000051, 1), (3003051, 1), (3004051, 1), // story
        (3000002, 1), (3001004, 1),
        (3002051, 1), (3003011, 1), (3003091, 1), (3004091, 1), // more area bosses

    ];

    [Fact]
    public async Task Measure_drop_rates()
    {
        string? output = Environment.GetEnvironmentVariable("KNS_DROP_SIM");
        if (string.IsNullOrEmpty(output)) return;
        int runs = int.TryParse(Environment.GetEnvironmentVariable("KNS_DROP_SIM_RUNS"), out int n) ? n : 1000;

        long user = (await server.CallGatewayAsync(51, new { hub_did = "drop-rate-simulation" }))["user_no"]!.GetValue<long>();
        await server.CallGatewayAsync(60, new { }, user);
        server.Execute($"INSERT OR REPLACE INTO tutorial_progress(user_no, quest_id) VALUES({user}, 0)");
        var report = new JsonObject { ["runs"] = runs, ["user"] = user };
        // KNS_DROP_SIM_STAGES=3003051,3004001 limits the run to those stages.
        HashSet<long>? only = Environment.GetEnvironmentVariable("KNS_DROP_SIM_STAGES")?.Split(',').Select(long.Parse).ToHashSet();
        foreach ((long stage, long mode) in Stages.Where(s => only is null || only.Contains(s.Stage)))
        {
            bool isEvent = mode == 11;
            if (isEvent)
                server.Execute($"INSERT OR REPLACE INTO master_event_schedule(stage_id,start_time,end_time,duration_seconds,period_seconds) VALUES({stage},0,2140000000,86400,86400)");
            var counts = new Dictionary<string, long>();
            long gold = 0, refused = 0, boxes = 0;
            for (int run = 0; run < runs; run++)
            {
                server.Execute($"UPDATE player_stamina SET stamina=999 WHERE user_no={user}");
                server.Execute($"DELETE FROM item WHERE user_no={user}");
                server.Execute($"DELETE FROM fellow WHERE user_no={user}");
                server.Execute($"DELETE FROM user_goods WHERE user_no={user}");
                long goldBefore = server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}");
                JsonObject start = await server.CallGatewayAsync(71, new { friend_type = 0, stage_id = stage, stage_mode = mode, friend_user_no = 0 }, user);
                if (start["errno"]!.GetValue<long>() != 0) { refused++; continue; }
                boxes += server.Scalar($"SELECT COUNT(*) FROM stage_event_offer WHERE user_no={user} AND item_id=1900015");
                JsonObject result = await server.CallGatewayAsync(72, new
                {
                    stage_no = isEvent ? stage + 1 : stage, stage_mode = mode, stage_result = 1, reward_gold = 0,
                    reward_items = Array.Empty<long>(), event_items = Array.Empty<long>(),
                }, user);
                if (result["errno"]!.GetValue<long>() != 0) { refused++; continue; }
                gold += server.Scalar($"SELECT gold FROM gameuser WHERE user_no={user}") - goldBefore;
                // Per-run spread of World Tree Branches (user_goods is emptied before every run).
                long branches = server.Scalar($"SELECT COALESCE(SUM(count),0) FROM user_goods WHERE user_no={user} AND item_id=1901007");
                counts[$"branch_runs:{branches}"] = counts.GetValueOrDefault($"branch_runs:{branches}") + 1;
                foreach (string sql in new[]
                {
                    $"SELECT 'item:' || item_id, COUNT(*) FROM item WHERE user_no={user} AND claimed=1 GROUP BY item_id",
                    $"SELECT 'ally:' || fellow_id, COUNT(*) FROM fellow WHERE user_no={user} GROUP BY fellow_id",
                    $"SELECT 'goods:' || item_id, SUM(count) FROM user_goods WHERE user_no={user} GROUP BY item_id",
                })
                {
                    foreach ((string key, long count) in server.QueryPairs(sql))
                        counts[key] = counts.GetValueOrDefault(key) + count;
                }
            }
            var row = new JsonObject { ["gold"] = gold, ["gold_boxes"] = boxes, ["refused"] = refused };
            foreach ((string key, long count) in counts.OrderBy(pair => pair.Key)) row[key] = count;
            report[stage.ToString()] = row;
        }
        File.WriteAllText(output, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
