using System.Text.Json.Serialization;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Rewards;

namespace KnS.Game.Protocols.Profile;

/// <summary>
/// Protocol 67 (sent from NetworkEvent@0x153548; reply case @0x15e1b6 reads day and reward_type into gameInfo+0xc458/+0xc470).
/// NetworkEvent@0x15390c copies them to the popup state and opens DrawPopupDailyReward for reward_type 1 or 2.
/// </summary>
public sealed class DailyRewardHandler(PlayerDirectory directory, LoginRewardService rewards) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        LoginRewardResult result = rewards.Claim(directory.ResolveCurrent(request).UserNumber);
        return ProtocolResult.Success(new DailyRewardResponse(result.Day, result.RewardType));
    }
}

public sealed record DailyRewardResponse(
    [property: JsonPropertyName("day")] long Day,
    [property: JsonPropertyName("reward_type")] long RewardType);

/// <summary>
/// Protocols 602/603: FinishTutorial@0x10c09c sends one when a tutorial ends. Both reply cases (@0x15d660, @0x15d612)
/// read user_info and update_item_etc_info. The request carries quest_id; native dt_quest_140 rewards are credited
/// directly to the wallet once per account (5000002 = 30 Gems, 5000005 = 1,000 Gold, 5000012 = 1,000 Gold).
/// </summary>
public sealed class TutorialRewardHandler(PlayerDirectory directory, PlayerProfileService profiles, LoginRewardService rewards)
    : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        Player player = directory.ResolveCurrent(request);
        rewards.FinishTutorialQuest(player.UserNumber, request.Parameters.GetInt64("quest_id"));
        return ProtocolResult.Success(new TutorialRewardResponse(profiles.BuildUserInfo(player), profiles.GetGoods(player.UserNumber)));
    }
}

public sealed record TutorialRewardResponse(
    [property: JsonPropertyName("user_info")] UserInfo User,
    [property: JsonPropertyName("update_item_etc_info")] IReadOnlyList<GoodsEntry> Goods);

/// <summary>
/// Protocol 601 at login: reply case @0x15aa08 parses quest_infos[0] (ParsingDataToQuestInfo: quest_type, quest_sub_type,
/// quest_id, action_count, try_count, complete). NetworkEvent@0x153940 starts tutorial quest_id (StartTutorial); an empty
/// list marks the main tutorials finished, which is what a player who completed the tutorial gets.
/// </summary>
public sealed class QuestListHandler(
    PlayerDirectory directory,
    LoginRewardService rewards,
    KnS.Game.Stages.StageProgressService progress,
    KnS.Data.Repositories.FellowRepository fellows) : IProtocolHandler
{
    private const long FirstDungeonStage = 3000001;

    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        // Legacy accounts with no tutorial_progress row that already progressed past stage 1 (cleared another stage
        // or own an ally) skip the intro chain so scripted popups do not block them.
        if (!rewards.HasTutorialProgress(userNumber)
            && (progress.GetClears(userNumber).Any(c => c.StageId != FirstDungeonStage) || fellows.GetOwnedFellowNumbers(userNumber).Count > 0))
        {
            rewards.SkipTutorialChain(userNumber);
        }

        long quest = rewards.CurrentTutorialQuest(userNumber);
        return ProtocolResult.Success(new QuestListResponse(quest > 0 ? [new QuestInfo(quest)] : []));
    }
}

public sealed record QuestListResponse([property: JsonPropertyName("quest_infos")] IReadOnlyList<QuestInfo> Quests);

public sealed record QuestInfo(
    [property: JsonPropertyName("quest_id")] long QuestId,
    [property: JsonPropertyName("quest_type")] long QuestType = 0,
    [property: JsonPropertyName("quest_sub_type")] long QuestSubType = 0,
    [property: JsonPropertyName("action_count")] long ActionCount = 0,
    [property: JsonPropertyName("try_count")] long TryCount = 0,
    [property: JsonPropertyName("complete")] long Complete = 0);
