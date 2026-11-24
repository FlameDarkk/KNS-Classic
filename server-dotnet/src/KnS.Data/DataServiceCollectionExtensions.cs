using KnS.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database, one <see cref="DataSession"/> per scope (opened on first use) and
    /// the repositories over it.
    /// </summary>
    public static IServiceCollection AddKnSData(this IServiceCollection services, DatabaseOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<GameDatabase>();
        services.AddScoped(provider => provider.GetRequiredService<GameDatabase>().OpenSession());

        services.AddScoped<PlayerRepository>();
        services.AddScoped<HeroRepository>();
        services.AddScoped<ItemRepository>();
        services.AddScoped<GoodsRepository>();
        services.AddScoped<BuffRepository>();
        services.AddScoped<SessionRepository>();
        services.AddScoped<LoginAccountRepository>();
        services.AddScoped<AccountSessionRepository>();
        services.AddScoped<FellowItemRepository>();
        services.AddScoped<FriendRepository>();
        services.AddScoped<FellowMissionRepository>();
        services.AddScoped<PvpRepository>();
        services.AddScoped<RaidRepository>();
        services.AddScoped<MailRepository>();
        services.AddScoped<ChatHistoryRepository>();
        services.AddScoped<ChatPinRepository>();
        services.AddScoped<LoginRewardRepository>();
        services.AddScoped<DailyQuestRepository>();
        services.AddScoped<AchievementRepository>();
        services.AddScoped<StoryRepository>();
        services.AddScoped<BlackMarketRepository>();
        services.AddScoped<FellowRepository>();
        services.AddScoped<FellowDrawAuditRepository>();
        services.AddScoped<StageRepository>();
        services.AddScoped<EventStageRepository>();
        services.AddScoped<MasterDataRepository>();
        services.AddScoped<PlatformAccountRepository>();
        services.AddScoped<TesterRepository>();
        services.AddScoped<FeatureFlagRepository>();
        return services;
    }
}
