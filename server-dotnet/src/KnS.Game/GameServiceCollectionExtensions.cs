using KnS.Game.Access;
using KnS.Game.Chat;
using KnS.Game.Fellows;
using KnS.Game.Friends;
using KnS.Game.Missions;
using KnS.Game.Gateway;
using KnS.Game.Inventory;
using KnS.Game.Players;
using KnS.Game.Protocols.Achievements;
using KnS.Game.Protocols.Arena;
using KnS.Game.Protocols.Fellows;
using KnS.Game.Protocols.Friends;
using KnS.Game.Protocols.Heroes;
using KnS.Game.Protocols.Items;
using KnS.Game.Protocols.Mail;
using KnS.Game.Protocols.Missions;
using KnS.Game.Protocols.Profile;
using KnS.Game.Protocols.Quests;
using KnS.Game.Protocols.Raid;
using KnS.Game.Protocols.Session;
using KnS.Game.Protocols.Shop;
using KnS.Game.Protocols.Stages;
using KnS.Game.Randomness;
using KnS.Game.Rewards;
using KnS.Game.Stages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KnS.Game;

public static class GameServiceCollectionExtensions
{
    /// <summary>Registers the game gateway, its services and one handler per protocol (needs the data services).</summary>
    public static IServiceCollection AddKnSGame(this IServiceCollection services, GameOptions options)
    {
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new ChatRelay(scopeFactory: sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddSingleton<IRandomSource>(options.RandomSeed is { } seed
            ? new SeededRandomSource(seed)
            : new SystemRandomSource());

        services.AddScoped<GatewayTrace>();
        services.AddScoped<PlayerDirectory>();
        services.AddScoped<SessionService>();
        services.AddScoped<PlayerAllowlist>();
        services.AddScoped<FeatureFlags>();
        services.AddScoped<PlayerProfileService>();
        services.AddScoped<InventoryService>();
        services.AddScoped<HeroGearRules>();
        services.AddScoped<ItemOptionRoller>();
        services.AddScoped<FellowService>();
        services.AddScoped<MonsterInfoBuilder>();
        services.AddScoped<StageProgressService>();
        services.AddScoped<EventStageService>();
        services.AddScoped<StageRewardService>();
        services.AddScoped<StoryService>();
        services.AddScoped<BagExpander>();
        services.AddScoped<FellowItemService>();
        services.AddScoped<FriendService>();
        services.AddScoped<LoginRewardService>();
        services.AddScoped<FellowMissionService>();
        services.AddScoped<ArenaService>();
        services.AddScoped<KnS.Game.Rewards.RewardGrantService>();
        services.AddScoped<RaidService>();
        services.AddScoped<RaidChallengeService>();
        services.AddScoped<GameGateway>();

        AddHandler<VersionCheckHandler>(services, GameProtocol.VersionCheck);
        AddHandler<LoginHandler>(services, GameProtocol.Login);
        AddHandler<HubUidHandler>(services, GameProtocol.HubUid);
        AddHandler<HubDeviceHandler>(services, GameProtocol.HubDevice);
        AddHandler<EventInfoHandler>(services, GameProtocol.EventInfo);
        AddHandler<LoadUserHandler>(services, GameProtocol.LoadUser);
        AddHandler<InventoryHandler>(services, GameProtocol.Inventory);
        AddHandler<MailListHandler>(services, GameProtocol.MailList);
        AddHandler<MailClaimHandler>(services, GameProtocol.MailClaim);
        AddHandler<StageInfoHandler>(services, GameProtocol.StageInfo);
        AddHandler<StageStartHandler>(services, GameProtocol.StageStart);
        AddHandler<StageResultHandler>(services, GameProtocol.StageResult);
        AddHandler<StageContinueHandler>(services, GameProtocol.StageContinue);
        AddHandler<AreaRewardHandler>(services, GameProtocol.AreaReward);
        AddHandler<EquipItemHandler>(services, GameProtocol.EquipItem);
        AddHandler<PowerUpItemHandler>(services, GameProtocol.PowerUpItem);
        AddHandler<EnhanceItemHandler>(services, GameProtocol.EnhanceItem);
        AddHandler<SellItemsHandler>(services, GameProtocol.SellItems);
        AddHandler<LockItemHandler>(services, GameProtocol.LockItem);
        AddHandler<FellowDrawHandler>(services, GameProtocol.FellowDraw);
        AddHandler<DailyQuestListHandler>(services, GameProtocol.DailyQuestList);
        AddHandler<DailyQuestClaimHandler>(services, GameProtocol.DailyQuestClaim);
        AddHandler<AchievementClaimHandler>(services, GameProtocol.AchievementClaim);
        AddHandler<AchievementListHandler>(services, GameProtocol.AchievementList);
        AddHandler<AchievementRewardListHandler>(services, GameProtocol.AchievementRewardList);
        AddHandler<AchievementDoingHandler>(services, GameProtocol.AchievementDoing);
        AddHandler<StoryListHandler>(services, GameProtocol.StoryList);
        AddHandler<StoryCompleteHandler>(services, GameProtocol.StoryComplete);
        AddHandler<ItemDrawHandler>(services, GameProtocol.ItemDraw);
        AddHandler<HeroPointShopBuyHandler>(services, GameProtocol.HeroPointShopBuy);
        AddHandler<DailyRewardHandler>(services, GameProtocol.DailyReward);
        AddHandler<QuestListHandler>(services, GameProtocol.QuestList);
        AddHandler<TutorialRewardHandler>(services, GameProtocol.TutorialFinish);
        AddHandler<TutorialRewardHandler>(services, GameProtocol.TutorialFinishSpecial);
        services.AddScoped<FellowEvolveHandler>();
        services.AddKeyedScoped<IProtocolHandler>((int)GameProtocol.FellowEvolve,
            (provider, _) => provider.GetRequiredService<FellowEvolveHandler>());
        AddHandler<FellowTalentResetHandler>(services, GameProtocol.FellowTalentReset);
        AddHandler<MaxStaminaUpgradeHandler>(services, GameProtocol.MaxStaminaUpgrade);
        AddHandler<StaminaRecoveryUpgradeHandler>(services, GameProtocol.StaminaRecoveryUpgrade);
        AddHandler<FriendshipShopBuyHandler>(services, GameProtocol.FriendshipShopBuy);
        AddHandler<GoldShopHandler>(services, GameProtocol.BuyGold);
        AddHandler<StaminaRefillHandler>(services, GameProtocol.StaminaRefill);
        AddHandler<BlackMarketHandler>(services, GameProtocol.BlackMarket);
        AddHandler<BlackMarketBuyHandler>(services, GameProtocol.BlackMarketBuy);
        AddHandler<FellowItemListHandler>(services, GameProtocol.FellowItemList);
        AddHandler<FellowItemEquipHandler>(services, GameProtocol.FellowItemEquip);
        AddHandler<FellowItemLockHandler>(services, GameProtocol.FellowItemLock);
        AddHandler<FellowItemSellHandler>(services, GameProtocol.FellowItemSell);
        AddHandler<FellowItemDrawHandler>(services, GameProtocol.FellowItemDraw);
        AddHandler<ExpandItemBagHandler>(services, GameProtocol.ExpandItemBag);
        AddHandler<HeroBuyHandler>(services, GameProtocol.HeroBuy);
        AddHandler<HeroChangeHandler>(services, GameProtocol.HeroChange);
        AddHandler<ExpandAllyBagHandler>(services, GameProtocol.ExpandAllyBag);
        AddHandler<ExpandFellowItemBagHandler>(services, GameProtocol.ExpandFellowItemBag);
        AddHandler<FellowSetHandler>(services, GameProtocol.FellowSet);
        AddHandler<FellowPowerUpHandler>(services, GameProtocol.FellowPowerUp);
        AddHandler<FellowLockHandler>(services, GameProtocol.FellowLock);
        AddHandler<FellowSellHandler>(services, GameProtocol.FellowSell);
        AddHandler<FellowEnhanceHandler>(services, GameProtocol.FellowEnhance);
        AddHandler<FellowLevelUpHandler>(services, GameProtocol.FellowLevelUp);
        AddHandler<UseBuffItemHandler>(services, GameProtocol.UseBuffItem);
        AddHandler<FellowBookRewardHandler>(services, GameProtocol.FellowBookReward);
        AddHandler<FriendRequestHandler>(services, GameProtocol.FriendRequest);
        AddHandler<FriendRequestListHandler>(services, GameProtocol.FriendRequestList);
        AddHandler<RaidStatusHandler>(services, GameProtocol.RaidStatus);
        AddHandler<RaidStartHandler>(services, GameProtocol.RaidStart);
        AddHandler<RaidResultHandler>(services, GameProtocol.RaidResult);
        AddHandler<RaidRankingHandler>(services, GameProtocol.RaidRanking);
        AddHandler<RaidRankRewardHandler>(services, GameProtocol.RaidRewardTable);
        AddHandler<RaidChallengeClaimsHandler>(services, GameProtocol.RaidChallengeReward);
        AddHandler<RaidChallengesHandler>(services, GameProtocol.RaidChallenges);
        AddHandler<RaidChallengeClaimHandler>(services, GameProtocol.RaidChallengeClaim);
        AddHandler<ArenaBattleStartHandler>(services, GameProtocol.ArenaBattleStart);
        AddHandler<ArenaResultHandler>(services, GameProtocol.ArenaResult);
        AddHandler<ArenaRewardHandler>(services, GameProtocol.ArenaReward);
        AddHandler<ArenaSeasonHandler>(services, GameProtocol.ArenaSeason);
        AddHandler<ArenaRankingHandler>(services, GameProtocol.ArenaRanking);
        AddHandler<ArenaRecoverHandler>(services, GameProtocol.ArenaRecover);
        AddHandler<ArenaOpponentsHandler>(services, GameProtocol.ArenaOpponents);
        AddHandler<AltarCraftHandler>(services, GameProtocol.AltarCraft);
        AddHandler<FellowItemCraftHandler>(services, GameProtocol.FellowItemCraft);
        AddHandler<FellowItemPowerUpHandler>(services, GameProtocol.FellowItemPowerUp);
        AddHandler<FellowItemDisassembleHandler>(services, GameProtocol.FellowItemDisassemble);
        AddHandler<FellowMissionListHandler>(services, GameProtocol.FellowMissionList);
        AddHandler<FellowMissionRefreshHandler>(services, GameProtocol.FellowMissionRefresh);
        AddHandler<FellowMissionStartHandler>(services, GameProtocol.FellowMissionStart);
        AddHandler<FellowMissionClaimHandler>(services, GameProtocol.FellowMissionClaim);
        AddHandler<FellowMissionFinishHandler>(services, GameProtocol.FellowMissionFinish);
        AddHandler<FellowMissionCancelHandler>(services, GameProtocol.FellowMissionCancel);
        AddHandler<FriendRejectHandler>(services, GameProtocol.FriendReject);
        AddHandler<FriendDetailHandler>(services, GameProtocol.FriendDetail);
        AddHandler<FriendDenyHandler>(services, GameProtocol.FriendDeny);
        AddHandler<FriendAcceptHandler>(services, GameProtocol.FriendAccept);
        AddHandler<FriendDeleteHandler>(services, GameProtocol.FriendDelete);
        AddHandler<FriendIncomingHandler>(services, GameProtocol.FriendIncoming);
        AddHandler<FriendOutgoingHandler>(services, GameProtocol.FriendOutgoing);
        AddHandler<FriendSendHandler>(services, GameProtocol.FriendSend);
        AddHandler<SocialTypeHandler>(services, GameProtocol.SocialType);
        AddHandler<FriendListHandler>(services, GameProtocol.FriendList);
        AddHandler<ChatServerInfoHandler>(services, GameProtocol.ChatServerInfo);
        return services;
    }

    private static void AddHandler<THandler>(IServiceCollection services, GameProtocol protocol)
        where THandler : class, IProtocolHandler =>
        services.AddKeyedScoped<IProtocolHandler, THandler>((int)protocol);
}
