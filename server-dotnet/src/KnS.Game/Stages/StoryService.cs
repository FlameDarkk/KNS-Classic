using KnS.Game.Rewards;
using System.Text.Json.Nodes;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Inventory;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Protocols.Quests;
using KnS.Game.Rules;
using KnS.Protocol.Json;

namespace KnS.Game.Stages;

/// <summary>
/// The story chain of master_story_quest (dt_story_pkg_131): each story opens when its previous one is completed; one story is
/// active at a time. The client completes a story (152) when every questN_count reaches its goal (CheckStoryComplete@0x76958).
/// </summary>
public sealed class StoryService(
    StoryRepository stories,
    FellowRepository fellows,
    PlayerProfileService profiles,
    RewardGrantService rewardGrants)
{
    public const long DefeatFamily = 1, ClearStage = 3, CollectItem = 4, OwnAlly = 5;

    /// <summary>The stories from the first one up to the active one, in chain order (the active one last, unless all are done).</summary>
    public IReadOnlyList<StoryQuestRule> Chain(long userNumber)
    {
        Dictionary<long, StoryQuestRule> byPrevious = stories.All().GroupBy(rule => rule.PreviousId).ToDictionary(group => group.Key, group => group.First());
        HashSet<long> completed = stories.Completed(userNumber);
        var chain = new List<StoryQuestRule>();
        long previous = 0;
        while (byPrevious.TryGetValue(previous, out StoryQuestRule? rule) && chain.Count < 1000)
        {
            chain.Add(rule);
            if (!completed.Contains(rule.StoryId))
            {
                break;
            }

            previous = rule.StoryId;
        }

        return chain;
    }

    /// <summary>The story in progress, or null when every story is completed.</summary>
    public StoryQuestRule? Active(long userNumber)
    {
        IReadOnlyList<StoryQuestRule> chain = Chain(userNumber);
        return chain.Count > 0 && !stories.IsCompleted(userNumber, chain[^1].StoryId) ? chain[^1] : null;
    }

    /// <summary>The story_info row of the active story (the last story when all are done).</summary>
    public StoryRow Current(long userNumber)
    {
        IReadOnlyList<StoryQuestRule> chain = Chain(userNumber);
        return chain.Count > 0 ? Row(userNumber, chain[^1]) : new StoryRow(0);
    }

    public StoryRow Row(long userNumber, StoryQuestRule rule)
    {
        long[] counts = rule.Objectives.Select(objective => Progress(userNumber, rule, objective)).ToArray();
        return new StoryRow(rule.StoryId)
        {
            Complete = stories.IsCompleted(userNumber, rule.StoryId) ? 1 : 0,
            Quest1Count = counts[0],
            Quest2Count = counts[1],
            Quest3Count = counts[2],
            Quest4Count = counts[3],
        };
    }

    /// <summary>Counts what a stage result (72) did toward the active story's objectives.</summary>
    public void RecordStageResult(long userNumber, long stageId, bool cleared, IReadOnlyList<JsonNode?> achievements, IReadOnlyList<EventOfferRecord> credited)
    {
        if (Active(userNumber) is not { } rule)
        {
            return;
        }

        foreach (StoryObjective objective in rule.Objectives.Where(objective => objective.Goal > 0))
        {
            long gained = objective.Type switch
            {
                // UpdateAchieveDungeon emits 43000 + family for the monster-family kill count; duplicates use the maximum.
                DefeatFamily => achievements.OfType<JsonObject>()
                    .Where(row => LenientJson.IsInteger(row["type"], out long type) && type == 43000 + objective.Target)
                    .Select(row => LenientJson.IsInteger(row["count"], out long count) ? Math.Max(0, count) : 0)
                    .DefaultIfEmpty(0).Max(),
                ClearStage => cleared && stageId == objective.Stage ? 1 : 0,
                CollectItem => credited.Where(offer => offer.ItemId == objective.Target).Sum(offer => offer.Count),
                _ => 0,
            };
            long remaining = objective.Goal - stories.Count(userNumber, rule.StoryId, objective.Index);
            if (gained > 0 && remaining > 0)
            {
                stories.Add(userNumber, rule.StoryId, Math.Min(gained, remaining), objective.Index);
            }
        }
    }

    /// <summary>
    /// Counts items gained outside a stage (ally mission rewards) toward the active story's "Gather" objectives. Restoration choice:
    /// only stage pickups counted before, so Mermaid's Tears, which mostly come from ally missions, never counted.
    /// </summary>
    public void RecordGathered(long userNumber, IEnumerable<(long ItemId, long Count)> gained)
    {
        if (Active(userNumber) is not { } rule)
        {
            return;
        }

        List<(long ItemId, long Count)> items = gained.ToList();
        foreach (StoryObjective objective in rule.Objectives.Where(objective => objective.Type == CollectItem && objective.Goal > 0))
        {
            long amount = items.Where(item => item.ItemId == objective.Target).Sum(item => Math.Max(0, item.Count));
            long remaining = objective.Goal - stories.Count(userNumber, rule.StoryId, objective.Index);
            if (amount > 0 && remaining > 0)
            {
                stories.Add(userNumber, rule.StoryId, Math.Min(amount, remaining), objective.Index);
            }
        }
    }

    /// <summary>Completes the active story when every objective is met and pays its rewards once. Null when not ready.</summary>
    public StoryRewards? Complete(long userNumber, long storyId)
    {
        if (Active(userNumber) is not { } rule || rule.StoryId != storyId
            || rule.Objectives.Any(objective => objective.Goal > 0 && Progress(userNumber, rule, objective) < objective.Goal)
            || !stories.Complete(userNumber, storyId))
        {
            return null;
        }

        profiles.AwardExperience(userNumber, profiles.ApplyExpBoost(userNumber, rule.Experience));
        var granted = new GrantedRewards();
        foreach ((long item, long count) in rule.Rewards)
        {
            rewardGrants.Grant(userNumber, item, count, granted);
        }

        return new StoryRewards(rule, granted.FellowNumbers, granted.ItemNumbers);
    }

    private long Progress(long userNumber, StoryQuestRule rule, StoryObjective objective) => objective.Type switch
    {
        0 => 0,
        OwnAlly => fellows.GetBook(userNumber).Any(row => row.FellowId == objective.Target) ? Math.Min(1, objective.Goal) : 0,
        _ => Math.Min(objective.Goal, stories.Count(userNumber, rule.StoryId, objective.Index)),
    };
}

public sealed record StoryRewards(StoryQuestRule Rule, IReadOnlyList<long> FellowNumbers, IReadOnlyList<long> ItemNumbers);
