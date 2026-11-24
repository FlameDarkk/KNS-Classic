using KnS.Game.Fellows;
using KnS.Game.Inventory;
using KnS.Game.Stages;
using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Gateway;
using KnS.Game.Players;
using KnS.Game.Models;
using KnS.Game.Protocols.Arena;
using KnS.Game.Rules;

namespace KnS.Game.Protocols.Quests;

/// <summary>
/// Protocol 141: today's daily quests (parser case @0x15dcea). An empty <c>daily_quest_infos</c> makes the parser send 140 (@0x161b36), whose
/// post-handler (@0x155356) sends 141 again: a bare reply loops forever behind "Connecting to server". So this always lists every quest.
/// Quest progress resets at UTC midnight; the original server's reset time is not known.
/// </summary>
public sealed class DailyQuestListHandler(DailyQuestRepository quests, PlayerDirectory directory, TimeProvider clock) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        long day = now / 86400;
        Dictionary<long, DailyQuestProgress> progress = quests.Get(userNumber, day).ToDictionary(row => row.QuestId);
        return ProtocolResult.Success(new DailyQuestListResponse(quests.Rules().Select(rule =>
        {
            progress.TryGetValue(rule.QuestId, out DailyQuestProgress? row);
            return new DailyQuestInfo(rule.QuestId, Math.Min(rule.Goal, row?.Count ?? 0), row?.Complete ?? 0);
        }).ToList(), (day + 1) * 86400 - now));
    }
}

public sealed record DailyQuestListResponse(
    [property: JsonPropertyName("daily_quest_infos")] IReadOnlyList<DailyQuestInfo> Quests,
    [property: JsonPropertyName("remain_time")] long RemainTime);

public sealed record DailyQuestInfo(
    [property: JsonPropertyName("quest_id")] long QuestId,
    [property: JsonPropertyName("quest_count")] long Count,
    [property: JsonPropertyName("complete")] long Complete)
{
    public static DailyQuestInfo Current(DailyQuestRepository quests, TimeProvider clock, long userNumber, long questId)
    {
        long day = clock.GetUtcNow().ToUnixTimeSeconds() / 86400;
        DailyQuestProgress? row = quests.Get(userNumber, day).FirstOrDefault(item => item.QuestId == questId);
        long goal = quests.Rules().Single(item => item.QuestId == questId).Goal;
        return new DailyQuestInfo(questId, Math.Min(goal, row?.Count ?? 0), row?.Complete ?? 0);
    }
}

/// <summary>Protocol 143 claims a completed quest once and returns both changed quest rows.</summary>
public sealed class DailyQuestClaimHandler(
    DailyQuestRepository quests, PlayerDirectory directory, PlayerProfileService profiles,
    KnS.Game.Rewards.RewardGrantService rewards, TimeProvider clock) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long questId = request.Parameters.GetInt64("quest_id");
        long day = clock.GetUtcNow().ToUnixTimeSeconds() / 86400;
        DailyQuestRule? rule = quests.Rules().FirstOrDefault(row => row.QuestId == questId);
        if (rule is null || !quests.Claim(userNumber, day, questId, rule.Goal))
        {
            return ProtocolResult.Refused("quest is not ready to collect");
        }

        rewards.Grant(userNumber, rule.RewardItemId, rule.RewardCount);

        const long completeQuestId = 5300001;
        if (questId != completeQuestId)
        {
            quests.Add(userNumber, day, completeQuestId, 1);
        }

        Dictionary<long, DailyQuestProgress> progress = quests.Get(userNumber, day).ToDictionary(row => row.QuestId);
        DailyQuestInfo Row(long id)
        {
            DailyQuestRule rowRule = quests.Rules().Single(row => row.QuestId == id);
            progress.TryGetValue(id, out DailyQuestProgress? row);
            return new DailyQuestInfo(id, Math.Min(rowRule.Goal, row?.Count ?? 0), row?.Complete ?? 0);
        }

        return ProtocolResult.Success(new DailyQuestClaimResponse(Row(questId), Row(completeQuestId), profiles.GetGoods(userNumber)));
    }
}

public sealed record DailyQuestClaimResponse(
    [property: JsonPropertyName("daily_quest_info")] DailyQuestInfo Quest,
    [property: JsonPropertyName("complete_daily_quest_info")] DailyQuestInfo CompleteQuest,
    [property: JsonPropertyName("update_item_etc_info")] IReadOnlyList<GoodsEntry> Goods);

/// <summary>
/// Protocol 151: the story list (parser case @0x161192, UpdateDataStoryInfo): the completed stories of the chain and the active one
/// (<see cref="StoryService"/>; 34 rows of dt_story_pkg_131).
/// </summary>
public sealed class StoryListHandler(PlayerDirectory directory, StoryService stories) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        return ProtocolResult.Success(new StoryListResponse(stories.Chain(userNumber).Select(rule => stories.Row(userNumber, rule)).ToList()));
    }
}

public sealed record StoryListResponse(
    [property: JsonPropertyName("story_infos")] IReadOnlyList<StoryRow> Stories);

public sealed record StoryRow(
    [property: JsonPropertyName("story_id")] long StoryId)
{
    [JsonPropertyName("complete")] public long Complete { get; init; }
    [JsonPropertyName("quest1_count")] public long Quest1Count { get; init; }
    [JsonPropertyName("quest2_count")] public long Quest2Count { get; init; }
    [JsonPropertyName("quest3_count")] public long Quest3Count { get; init; }
    [JsonPropertyName("quest4_count")] public long Quest4Count { get; init; }
    [JsonPropertyName("story_open_remain_time")] public long OpensInSeconds { get; init; }
}

/// <summary>
/// Protocol 152: complete the active story once its objectives are met (parser @0x161348: complete_story_info, story_info,
/// user_info, update_item_etc_info, fellow_info, fellow_book_info, item_info), pay its rewards and open the next one.
/// </summary>
public sealed class StoryCompleteHandler(
    PlayerDirectory directory, StoryService stories, PlayerProfileService profiles, FellowService fellows,
    InventoryService inventory, ItemRepository items) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        Player player = directory.ResolveCurrent(request);
        long userNumber = player.UserNumber;
        long storyId = request.Parameters.GetInt64("story_id");
        if (stories.Complete(userNumber, storyId) is not { } rewards)
        {
            return ProtocolResult.Refused("story is not ready to complete");
        }

        IReadOnlyList<FellowInfo> newFellows = rewards.FellowNumbers.Count > 0
            ? fellows.GetFellowInfos(userNumber, rewards.FellowNumbers.ToHashSet()) : [];
        return ProtocolResult.Success(new StoryCompleteResponse
        {
            Completed = stories.Row(userNumber, rewards.Rule),
            Next = stories.Current(userNumber),
            User = profiles.BuildUserInfo(player),
            Goods = profiles.GetGoods(userNumber),
            Fellows = newFellows,
            FellowBook = newFellows.Count > 0 ? fellows.GetBook(userNumber, newFellows.Select(f => f.FellowId).ToHashSet()) : [],
            Items = rewards.ItemNumbers.Select(number => items.FindOwned(userNumber, number)).OfType<ItemRecord>()
                .Select(item => inventory.ToItemInfo(item)).ToList(),
        });
    }
}

public sealed record StoryCompleteResponse
{
    [JsonPropertyName("complete_story_info")] public required StoryRow Completed { get; init; }
    [JsonPropertyName("story_info")] public required StoryRow Next { get; init; }
    [JsonPropertyName("user_info")] public required UserInfo User { get; init; }
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("fellow_info")] public IReadOnlyList<FellowInfo> Fellows { get; init; } = [];
    [JsonPropertyName("fellow_book_info")] public IReadOnlyList<FellowBookEntry> FellowBook { get; init; } = [];
    [JsonPropertyName("item_info")] public IReadOnlyList<ItemInfo> Items { get; init; } = [];
}
