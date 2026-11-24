using KnS.Data;
using KnS.Data.Repositories;
using KnS.Game.Rewards;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

/// <summary>
/// Every reward id in the data that a claim pays must be an ally, ally gear, an inventory item or a currency the server knows how
/// to pay (<see cref="RewardGrantService"/>). Guards against a reward silently landing where the player cannot see it.
/// </summary>
[Collection(SharedServer.Name)]
public class RewardWiringTests(ServerFixture server)
{
    /// <summary>(table, reward id columns) of the claim paths: achievements, daily quests, story, ally missions, area,
    /// raid challenges and rank rewards, login rewards, Hero Point shop.</summary>
    public static readonly TheoryData<string, string> RewardColumns = new()
    {
        { "master_achievement", "reward_item_id" },
        { "master_daily_quest", "reward_item_id" },
        { "master_story_quest", "reward1_item,reward2_item,reward3_item,reward4_item" },
        { "master_fellow_mission", "reward1_item,reward2_item,reward3_item,reward4_item,reward5_item,reward6_item" },
        { "master_stage_attainment", "normal_reward_item_id,hard_reward_item_id" },
        { "master_raid_challenge", "reward_item_id" },
        { "master_raid_rank_reward", "reward1_item,reward2_item" },
        { "master_login_reward", "item_id" },
        { "master_hero_point_shop", "reward_item_id" },
    };

    [Theory]
    [MemberData(nameof(RewardColumns))]
    public void Every_reward_id_has_a_known_owner(string table, string columns)
    {
        using IServiceScope scope = server.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<DataSession>();
        var rewards = scope.ServiceProvider.GetRequiredService<RewardGrantService>();
        var ids = new SortedSet<long>();
        foreach (string column in columns.Split(','))
        {
            ids.UnionWith(session.Query<long>($"SELECT DISTINCT {column} FROM {table} WHERE {column} > 0"));
        }

        Assert.NotEmpty(ids);
        Assert.Empty(ids.Where(id => rewards.Classify(id) == RewardKind.Unknown));
    }

    [Fact]
    public void Kinds_go_to_their_own_owner()
    {
        using IServiceScope scope = server.CreateScope();
        var rewards = scope.ServiceProvider.GetRequiredService<RewardGrantService>();
        Assert.Equal(RewardKind.Ally, rewards.Classify(100322));
        Assert.Equal(RewardKind.Stamina, rewards.Classify(1900014));
        Assert.Equal(RewardKind.BattleSpirit, rewards.Classify(1900017));
        Assert.Equal(RewardKind.Counted, rewards.Classify(1901007));
        Assert.Equal(RewardKind.Unknown, rewards.Classify(4));
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<GoodsRepository>().Add(1, 100322, 1));
    }
}
