using KnS.Data.Repositories;
using KnS.Game.Missions;
using Ally = KnS.Game.Missions.FellowMissionService.MissionAlly;

namespace KnS.Tests.Game;

/// <summary>The server's mission chance must equal the one the client shows (GetSuccessRate@0x563d8).</summary>
public class MissionSuccessRateTests
{
    private static readonly FellowMissionRule Rule = new()
    {
        Grade = 2, AllyCount = 2, Level = 3, AllyType = 2, Conditions = [6220160, 6220320],
    };

    [Fact]
    public void Scores_allies_conditions_by_best_ally_and_adds_mercenaries()
    {
        // A: grade 4 (8) + Lv.22 (8) + power-up +2 (8) + country (5) + type (5) + condition 2 at level 1 (2) = 36.
        var a = new Ally(4, 22, 2, 3, 2, [6220162, 6220321]);
        // B: grade 3 (4) + Lv.10 (4) + type (5) + condition 1 at level 3, above A's level 2 (10) = 23.
        var b = new Ally(3, 10, 0, 1, 2, [6220163]);
        Assert.Equal(73, FellowMissionService.SuccessRate(Rule, [a, b], []));
        // Mercenaries of type 1 and 4: 5 + 20.
        Assert.Equal(98, FellowMissionService.SuccessRate(Rule, [a, b], [1, 4]));
        Assert.Equal(100, FellowMissionService.SuccessRate(Rule, [a, b], [5, 5]));
    }

    [Fact]
    public void Skill_ids_without_a_level_digit_meet_no_condition()
    {
        var ally = new Ally(1, 1, 0, 0, 0, [6220160, -1]);
        // Grade 1 (1) + Lv.1 (4) = 5 of the 2 x 40 target.
        Assert.Equal(6, FellowMissionService.SuccessRate(Rule, [ally], []));
        Assert.Equal(15, FellowMissionService.SuccessRate(Rule, [], [3]));
    }
}
