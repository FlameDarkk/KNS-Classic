using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Access;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Rules;
using KnS.Game.Stages;
using KnS.Protocol;
using KnS.Protocol.Json;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Stages;

/// <summary>
/// Protocol 72: dungeon stage result (case @0x15fcd0). Pays the gold the client counted, claims
/// the picked-up chests (reward_items) and event items (event_items), and on a clear awards exp,
/// fellow exp and the rolled fellows.
/// </summary>
public sealed class StageResultHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    StageRewardService rewards,
    StageProgressService progress,
    FellowService fellows,
    GatewayTrace trace,
    DailyQuestRepository quests,
    StoryService storyService,
    MasterDataRepository masterData,
    EventStageRepository eventStages,
    TimeProvider clock,
    ILogger<StageResultHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        RequestParameters parameters = request.Parameters;
        Player player = directory.ResolveCurrent(request);
        long userNumber = player.UserNumber;
        long stageId = parameters.GetInt64("stage_no");
        StageStartRecord? open = rewards.GetStart(userNumber);
        if (open is not null && stageId != open.StageId
            && !(EventStageService.IsEventStage(open.StageId) && stageId == open.StageId + 1))
            return ProtocolResult.Refused("stage result does not match the open attempt");
        if (EventStageService.IsEventStage(stageId))
        {
            long? started = eventStages.CompletePlay(userNumber, stageId);
            if (started is null) return ProtocolResult.Refused("no matching event dungeon attempt");
            // Event runs contain a distinct boss segment N+1. Rewards and EXP belong to selected stage N.
            stageId = started.Value;
        }
        PendingDrops offered = rewards.GetPendingDrops(userNumber);
        StageStartRecord? start = rewards.TakeStart(userNumber);
        if (start is null)
            return ProtocolResult.Success(new StageResultResponse
            {
                StageResult = parameters.CopyOrDefault("stage_result", 1),
                DailyQuest = Quests.DailyQuestInfo.Current(quests, clock, userNumber, 5300002),
                Story = storyService.Current(userNumber), UserInfo = profiles.BuildUserInfo(player), Goods = profiles.GetGoods(userNumber),
            });

        // The gold piles the client counted; a value that is not a number pays nothing. Capped at the most the stage's
        // piles can hold (dt_stage_pkg_140 c108 * c109, at least DropRules.MaxStageGold), paid once per stage start.
        StageGoldSettings? goldSettings = masterData.GetStageGoldSettings(stageId);
        long stageGoldCap = Math.Max(DropRules.MaxStageGold, goldSettings is { PileMax: > 0, PileCount: > 0 } ? goldSettings.PileMax * goldSettings.PileCount : 0);
        long reportedGold = parameters.GetInt64OrFallback("reward_gold");
        if (reportedGold > stageGoldCap)
        {
            logger.LogWarning("Stage result: reward_gold={Gold} exceeds the cap {Cap}; paying the cap", reportedGold, stageGoldCap);
        }
        profiles.AddCurrency(userNumber, gold: Math.Clamp(reportedGold, 0, stageGoldCap));

        logger.LogInformation("Stage result: reward_gold={Gold}, gold now {Balance}; reward_items={Rewards} event_items={Events} max_damage={MaxDamage}",
            parameters["reward_gold"]?.ToJsonString(), profiles.GetAccount(userNumber).Gold,
            parameters["reward_items"]?.ToJsonString() ?? "null", parameters["event_items"]?.ToJsonString() ?? "null",
            parameters["max_damage"]?.ToJsonString());

        IReadOnlyList<JsonNode?> rewardItems = parameters.GetList("reward_items");
        bool cleared = !parameters.Contains("stage_result") || LenientJson.NumericallyEquals(parameters["stage_result"], 1);
        // On a clear every offered drop is granted, picked up or not.
        List<ItemInfo> claimedItems = rewards.ClaimItems(userNumber, rewardItems, claimRest: cleared);
        IEnumerable<long> reportedNumbers = parameters.GetList("event_items").Concat(rewardItems)
            .Select(node => LenientJson.IsInteger(node, out long number) ? number : (long?)null)
            .OfType<long>();
        List<long> droppedFellowNumbers = cleared ? rewards.GrantDroppedFellows(userNumber).ToList() : [];
        List<EventOfferRecord> credited = rewards.ClaimEventOffers(userNumber, reportedNumbers.ToList(), cleared).ToList();
        IReadOnlyList<object> chests = StageRewardService.OrderChests(rewardItems, claimedItems, credited,
            Math.Min(droppedFellowNumbers.Count, StageRewardService.MaxResultChests));
        long[] playedFellowNumbers = droppedFellowNumbers.Take(StageRewardService.MaxResultChests).ToArray();
        logger.LogInformation("Stage result: claimed items [{Items}], credited offers [{Offers}]",
            string.Join(",", claimedItems.Select(i => i.ItemNumber)), string.Join(",", credited.Select(o => $"{o.ItemId}x{o.Count}")));

        JsonNode? stageResult = parameters.CopyOrDefault("stage_result", 1);
        IReadOnlyList<object> fellowExperience = Enumerable.Repeat<object>(
            EmptyObject.Instance, FellowRules.SlotsPerSetup).ToList();
        storyService.RecordStageResult(userNumber, stageId, cleared, parameters.GetList("achievement_list"), credited);
        if (cleared)
        {
            long gain = profiles.AwardStageExperience(userNumber, stageId);
            PlayerAccount account = profiles.GetAccount(userNumber);
            logger.LogInformation("Stage result: +{Gain} exp -> Lv.{Level} {Exp}/{MaxExp}", gain, account.Level, account.Experience, account.MaxExperience);
            fellowExperience = fellows.AwardDungeonExperience(userNumber, gain);
            progress.RecordClear(userNumber, stageId, start.StageMode);
            if (stageId is >= 3000000 and < 3100000)
            {
                quests.Add(userNumber, clock.GetUtcNow().ToUnixTimeSeconds() / 86400, 5300002, 1);
            }
        }

        IReadOnlyList<FellowInfo> droppedFellows = playedFellowNumbers.Length > 0
            ? fellows.GetFellowInfos(userNumber, playedFellowNumbers.ToHashSet())
            : [];
        if (droppedFellows.Count > 0)
        {
            logger.LogInformation("Stage result: dropped fellows [{Fellows}]", string.Join(",", droppedFellows.Select(f => f.FellowId)));
        }

        trace.Notes["drops"] = StageDropAudit.Build(
            offered, rewardItems, parameters.GetList("event_items"), claimedItems, credited, cleared,
            droppedFellows.Select(f => f.FellowId).ToList(), parameters.UnreadNames, trace.Warnings);

        long? areaId = progress.GetAreaOfStage(stageId);
        object attainment = areaId is { } area && area != 0
            ? progress.GetAttainment(userNumber, area) ?? (object)EmptyObject.Instance
            : EmptyObject.Instance;

        return ProtocolResult.Success(new StageResultResponse
        {
            StageResult = stageResult,
            DailyQuest = Quests.DailyQuestInfo.Current(quests, clock, userNumber, 5300002),
            Story = storyService.Current(userNumber),
            UserInfo = profiles.BuildUserInfo(player),
            Goods = profiles.GetGoods(userNumber),
            EventDrops = credited.Where(offer => offer.ItemId > DropRules.MaxFellowItemId && offer.ItemId != CurrencyItems.GoldBox)
                .GroupBy(offer => offer.ItemId)
                .Select(group => new EventDropOffer(group.Key, group.Sum(offer => offer.Count), group.First().ItemNumber)).ToList(),
            Chests = chests,
            FellowExperience = fellowExperience,
            DroppedFellows = droppedFellows,
            FellowBook = fellows.GetBook(userNumber, droppedFellows.Select(f => f.FellowId).ToHashSet()),
            AreaAttainment = attainment,
        });
    }
}

public sealed record StageResultResponse
{
    [JsonPropertyName("daily_quest_info")] public required Quests.DailyQuestInfo DailyQuest { get; init; }
    [JsonPropertyName("story_info")] public required Quests.StoryRow Story { get; init; }
    [JsonPropertyName("stage_result")] public JsonNode? StageResult { get; init; }
    [JsonPropertyName("user_info")] public required UserInfo UserInfo { get; init; }
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];

    // Result-only credit, separate from the absolute wallet balance above. The
    // native reply buffer is cleared between protocols 71 and 72, so start offers
    // cannot be reused when drawing a result.
    [JsonPropertyName("drop_event_item_list")] public IReadOnlyList<EventDropOffer> EventDrops { get; init; } = [];

    /// <summary>The result screen's drops (one chest each): <see cref="ItemInfo"/> or <see cref="CountedItemInfo"/>.</summary>
    [JsonPropertyName("item_infos")] public IReadOnlyList<object> Chests { get; init; } = [];

    /// <summary>The dungeon setup's three slots after the exp: <see cref="FellowInfo"/> or {} for an empty slot.</summary>
    [JsonPropertyName("fellow_info")] public IReadOnlyList<object> FellowExperience { get; init; } = [];

    [JsonPropertyName("drop_fellow_infos")] public IReadOnlyList<FellowInfo> DroppedFellows { get; init; } = [];
    [JsonPropertyName("fellow_book_info")] public IReadOnlyList<FellowBookEntry> FellowBook { get; init; } = [];

    /// <summary>The stage's area counts (<see cref="AreaAttainment"/>), which replace the client's; {} when none.</summary>
    [JsonPropertyName("stage_attainment_info")] public object AreaAttainment { get; init; } = EmptyObject.Instance;
}
