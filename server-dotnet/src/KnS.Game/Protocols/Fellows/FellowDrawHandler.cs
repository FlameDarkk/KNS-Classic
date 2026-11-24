using System.Text.Json;
using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Rules;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Fellows;

/// <summary>
/// Protocol 92: ally draw, the gacha (case @0x15c7b4, post-handler @0x152a2a). The package
/// (master_shop_package) sets the price, in any currency, and the draws per purchase. The Hero Point Shop's packages fix the star grade.
/// </summary>
public sealed class FellowDrawHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    FellowService fellows,
    MasterDataRepository masterData,
    FellowDrawAuditRepository audit, TimeProvider clock,
    ILogger<FellowDrawHandler> logger) : IProtocolHandler
{
    private const long AllyTicketItem = 1900012;

    public ProtocolResult Handle(GatewayRequest request)
    {
        Player player = directory.ResolveCurrent(request);
        long userNumber = player.UserNumber;
        long packageId = request.Parameters.GetInt64("package_id");
        ShopPackage? package = masterData.GetShopPackage(packageId);
        // use_ticket: the Premium popup's ticket button pays one Ally Ticket (1900012, the item DrawGambleBoard shows)
        // per draw instead of the package price (restoration choice; no package row prices that ticket).
        bool useTicket = request.Parameters.GetInt64OrFallback("use_ticket") != 0;
        bool paid = package is not null && (useTicket
            ? profiles.TrySpend(userNumber, AllyTicketItem, package.DrawCount)
            : profiles.TrySpend(userNumber, package.PriceItemId, package.Price));
        if (package is null || !paid)
        {
            logger.LogInformation("Draw: package {Package} refused (ticket {Ticket}) ({Row})", packageId, useTicket, package);
            return ProtocolResult.Refused("cannot draw");
        }

        string username = audit.Username(userNumber);
        string batchId = Guid.NewGuid().ToString("N");
        long timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        long paymentItem = useTicket ? AllyTicketItem : package.PriceItemId;
        long paymentCount = useTicket ? package.DrawCount : package.Price;
        Dictionary<long, long> grades = masterData.GetDrawableFellows().ToDictionary(row => row.FellowId, row => row.Grade);
        var detail = new List<object>();
        var drawnNumbers = new HashSet<long>();
        var drawnIds = new HashSet<long>();
        for (long i = 0; i < package.DrawCount; i++)
        {
            long fellowId = fellows.RollDrawnFellow(ShopRules.FixedFellowGrade(packageId), ShopRules.IsPremiumFellowDraw(packageId), ShopRules.IsFriendshipFellowDraw(packageId));
            long fellowNumber = fellows.Grant(userNumber, fellowId);
            audit.Record(userNumber, username, player.Nickname, batchId, timestamp, "shop", packageId, paymentItem, paymentCount, fellowNumber, fellowId);
            detail.Add(new { fellow_id = fellowId, fellow_no = fellowNumber, grade = grades[fellowId] });
            drawnNumbers.Add(fellowNumber);
            drawnIds.Add(fellowId);
        }

        logger.LogInformation("Ally draw pending commit: {Audit}", JsonSerializer.Serialize(new
        {
            batch_id = batchId, user_no = userNumber, username, nickname = player.Nickname, package_id = packageId,
            payment_item = paymentItem, payment_count = paymentCount, fellows = detail,
        }));
        return ProtocolResult.Success(new FellowDrawResponse
        {
            Goods = profiles.GetGoods(userNumber),
            DrawnFellows = fellows.GetFellowInfos(userNumber, drawnNumbers),
            FellowBook = fellows.GetBook(userNumber, drawnIds),
        });
    }
}

public sealed record FellowDrawResponse
{
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];

    /// <summary>The drawn fellows. The draw animation dereferences the first one: an empty list crashes the game.</summary>
    [JsonPropertyName("fellow_info")] public IReadOnlyList<FellowInfo> DrawnFellows { get; init; } = [];

    [JsonPropertyName("fellow_book_info")] public IReadOnlyList<FellowBookEntry> FellowBook { get; init; } = [];
    [JsonPropertyName("social_reward_keys")] public IReadOnlyList<object> SocialRewardKeys { get; init; } = [];
    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
    [JsonPropertyName("image_file_download_url")] public string ImageFileDownloadUrl { get; init; } = "";
    [JsonPropertyName("story_info")] public StoryInfo Story { get; init; } = new();
    [JsonPropertyName("story_card_infos")] public IReadOnlyList<object> StoryCards { get; init; } = [];
}

public sealed record StoryInfo
{
    [JsonPropertyName("story_id")] public long StoryId { get; init; }
    [JsonPropertyName("quest1_count")] public long Quest1Count { get; init; }
    [JsonPropertyName("quest2_count")] public long Quest2Count { get; init; }
    [JsonPropertyName("quest3_count")] public long Quest3Count { get; init; }
    [JsonPropertyName("quest4_count")] public long Quest4Count { get; init; }
    [JsonPropertyName("story_open_remain_time")] public long OpensInSeconds { get; init; }
}
