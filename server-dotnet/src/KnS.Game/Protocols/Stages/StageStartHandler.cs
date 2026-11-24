using KnS.Data.Repositories;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Rules;
using KnS.Game.Stages;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Stages;

/// <summary>
/// Protocol 71: dungeon stage start (case @0x15e324). Request {friend_type, stage_id,
/// stage_mode, friend_user_no}; it carries no user_no.
/// </summary>
/// <remarks>
/// The reply must repeat user_info: the client runs UpdateUserInfo / UpdateUserGoods on every
/// stage reply, and a missing user_info copies zero gold and gems over the real balance.
/// </remarks>
public sealed class StageStartHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    MasterDataRepository masterData,
    MonsterInfoBuilder monsters,
    StageRewardService rewards,
    EventStageService events,
    EventStageRepository eventStages,
    DailyQuestRepository quests,
    KnS.Game.Friends.FriendService friends,
    KnS.Game.Inventory.InventoryService inventory,
    KnS.Game.Protocols.Raid.RaidService raid,
    TimeProvider clock,
    GatewayTrace trace,
    ILogger<StageStartHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long stageId = request.Parameters.GetInt64("stage_id");
        long stageMode = request.Parameters.GetInt64("stage_mode", 1);
        Player player = directory.ResolveCurrent(request);
        // Event Enter sends difficulty + 10 (11 through 15); stage_id selects its combat data.
        bool validMode = stageMode is 1 or 2 || (stageMode is >= 11 and <= 15 && EventStageService.IsEventStage(stageId));
        if (masterData.GetStageMonsterSetup(stageId) is null || !validMode)
        {
            return ProtocolResult.Refused("unknown stage or stage mode");
        }
        if (!events.CanEnter(stageId))
        {
            return ProtocolResult.Refused("event dungeon is not open");
        }
        IReadOnlyList<JsonObject> monsterRows = monsters.Build(stageId, stageMode);
        IReadOnlyList<JsonObject> secondMonsterRows = EventStageService.IsEventStage(stageId)
            ? monsters.Build(stageId + 1, stageMode) : monsterRows;
        long staminaCost = masterData.GetStageStaminaCost(stageId);
        if (!profiles.TrySpendStamina(player.UserNumber, staminaCost))
        {
            logger.LogInformation("Stage {Stage} refused: not enough stamina", stageId);
            return ProtocolResult.Refused("not enough stamina");
        }
        UserInfo userInfo = profiles.BuildUserInfo(player);
        IReadOnlyList<GoodsEntry> goods = profiles.GetGoods(player.UserNumber);
        DropItemList drops = rewards.OfferDrops(player.UserNumber, stageId, stageMode, clock.GetUtcNow().ToUnixTimeSeconds(), staminaCost);
        IReadOnlyList<EventDropOffer> eventDrops = rewards.OfferEventItems(player.UserNumber);
        Quests.DailyQuestInfo? dailyQuest = null;
        if (EventStageService.IsEventStage(stageId))
        {
            eventStages.BeginPlay(player.UserNumber, stageId);
            quests.Add(player.UserNumber, clock.GetUtcNow().ToUnixTimeSeconds() / 86400, 5300004, 1);
            dailyQuest = Quests.DailyQuestInfo.Current(quests, clock, player.UserNumber, 5300004);
        }
        logger.LogInformation("Stage {Stage} drops{Boss}: chests [{Chests}] boss [{BossDrops}], event items [{Events}]",
            stageId, drops.BossDrops.Count > 0 ? " (boss list)" : "",
            string.Join(",", drops.Chests.Select(d => d.ItemId)), string.Join(",", drops.BossDrops.Select(d => d.ItemId)),
            string.Join(",", eventDrops.Select(e => $"{e.ItemId}x{e.Count}")));
        // Every slot the roll hit; the fellows among them are granted by the result, not offered here.
        trace.Notes["rolled"] = StageDropAudit.DescribeRolled(rewards.GetPendingDrops(player.UserNumber).Rolled);

        return ProtocolResult.Success(new StageStartResponse
        {
            UserInfo = userInfo,
            Goods = goods,
            Stage = new StageSetup
            {
                StageNumber = stageId,
                // The client reads monster_info_<N> and <N+1>, N = the selected stage's id.
                MonsterInfo = new Dictionary<string, object>
                {
                    [$"monster_info_{stageId}"] = monsterRows,
                    [$"monster_info_{stageId + 1}"] = secondMonsterRows,
                },
            },
            Drops = drops,
            EventDrops = eventDrops,
            DailyQuest = dailyQuest,
            Helper = BuildHelper(player.UserNumber, request.Parameters.GetInt64("friend_user_no")),
        });
    }

    /// <summary>
    /// The chosen helper (friend_user_no). ParsingDataToDungeonFriendDetail@0x14cfe4 reads friend_detail_info; NetworkEvent@0x155c62
    /// copies its hero_info into fight+0x1d58 when friend_user_no &gt; 0, and the help button (Scene_PointerPressEvent_SC_FIELD
    /// 0x68588) and ProFieldLoading@0xc5a12 build the helper from it. Without it the summon draws an empty character.
    /// </summary>
    private DungeonFriendDetail? BuildHelper(long userNumber, long helperNumber)
    {
        if (helperNumber == TutorialHelperNumber) return BuildTutorialHelper();
        if (helperNumber <= 0 || helperNumber == userNumber || friends.Lookup([helperNumber]) is not [{ } helper, ..]) return null;
        friends.RewardHelper(userNumber, helperNumber);
        UserInfo info = profiles.BuildUserInfo(helper);
        IReadOnlyList<OwnedHeroRecord> owned = inventory.GetHeroes(helperNumber);
        OwnedHeroRecord? hero = owned.FirstOrDefault(h => h.HeroPcid == info.ActiveHeroPcid) ?? (owned.Count > 0 ? owned[0] : null);
        if (hero is null) return null;
        return new DungeonFriendDetail
        {
            FriendUserNumber = helperNumber,
            Level = info.Level,
            RaidRank = raid.GetUserInfo(helperNumber) is { Rank: > 0 } raidInfo ? raidInfo.Rank : 0,
            Hero = inventory.BuildHeroInfo(helperNumber, hero.HeroNumber, hero.HeroPcid),
        };
    }

    /// <summary>
    /// The tutorial's built-in helper: the first stage starts with friend_type 2 and friend_user_no 1, which is no player.
    /// DrawSelectHelper@0x6eb4a draws it as "Lv.60 Alaska" wearing Lion King (GetEquipDataByID 300091 @0x6ec84). The rest
    /// (Knight, Great Sword 200091, Rocky 400091 at master base stats) is a restoration choice.
    /// </summary>
    public const long TutorialHelperNumber = 1;

    private static readonly long[] TutorialHelperGear = [200091, 300091, 400091];

    private DungeonFriendDetail BuildTutorialHelper() => new()
    {
        FriendUserNumber = TutorialHelperNumber,
        Level = 60,
        Hero = new HeroInfo
        {
            UserNumber = TutorialHelperNumber,
            HeroNumber = 1,
            HeroPcid = 1,
            EquippedItems = TutorialHelperGear.Select((itemId, slot) =>
            {
                ItemStats stats = inventory.GetNewItemStats(itemId);
                return (object)new ItemInfo
                {
                    ItemNumber = slot + 1, ItemId = itemId, HeroNumber = 1,
                    Attack = stats.Attack, Defence = stats.Defence, Hp = stats.Hp,
                };
            }).ToList(),
        },
    };
}

/// <summary>friend_detail_info of protocol 71 (ParsingDataToDungeonFriendDetail@0x14cfe4). hero_* stats are parsed but not read back.</summary>
public sealed record DungeonFriendDetail
{
    [JsonPropertyName("friend_user_no")] public long FriendUserNumber { get; init; }
    [JsonPropertyName("raid_rank")] public long RaidRank { get; init; }
    [JsonPropertyName("user_level")] public long Level { get; init; }
    [JsonPropertyName("hero_attack")] public long Attack { get; init; }
    [JsonPropertyName("hero_defence")] public long Defence { get; init; }
    [JsonPropertyName("hero_hp")] public long Hp { get; init; }
    [JsonPropertyName("hero_info")] public required HeroInfo Hero { get; init; }
}

public sealed record StageStartResponse
{
    [JsonPropertyName("user_info")] public required UserInfo UserInfo { get; init; }
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("stage_info")] public required StageSetup Stage { get; init; }
    [JsonPropertyName("verify_info")] public VerifyInfo Verify { get; init; } = new();
    [JsonPropertyName("drop_item_list")] public required DropItemList Drops { get; init; }

    /// <summary>
    /// Two slots: the client flags <see cref="EventDropOffer.Count"/> random monsters per slot,
    /// which drop the slot's item when they die.
    /// </summary>
    [JsonPropertyName("drop_event_item_list")] public IReadOnlyList<EventDropOffer> EventDrops { get; init; } = [];
    [JsonPropertyName("daily_quest_info")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Quests.DailyQuestInfo? DailyQuest { get; init; }

    /// <summary>Absent without a helper: the parser then zeroes the block, the native no-helper state.</summary>
    [JsonPropertyName("friend_detail_info")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DungeonFriendDetail? Helper { get; init; }
}

public sealed record StageSetup
{
    [JsonPropertyName("stage_no")] public long StageNumber { get; init; }
    [JsonPropertyName("rand_key")] public long RandomKey { get; init; } = 1;
    [JsonPropertyName("drop_count")] public long GoldPileCount { get; init; } = DropRules.GoldPileCount;
    [JsonPropertyName("drop_gold_max")] public long GoldPileMax { get; init; } = DropRules.GoldPileMax;
    [JsonPropertyName("gold_gain_bonus")] public long GoldGainBonus { get; init; }

    /// <summary>monster_info_&lt;stage&gt; keys: the stage's 13 monster stat rows.</summary>
    [JsonExtensionData] public Dictionary<string, object> MonsterInfo { get; init; } = [];
}

public sealed record VerifyInfo
{
    [JsonPropertyName("verify_type")] public long VerifyType { get; init; }
    [JsonPropertyName("attack")] public long Attack { get; init; } = PlayerRules.HeroAttack;
    [JsonPropertyName("defence")] public long Defence { get; init; } = PlayerRules.HeroDefence;
    [JsonPropertyName("hp")] public long Hp { get; init; } = PlayerRules.HeroHp;
}
